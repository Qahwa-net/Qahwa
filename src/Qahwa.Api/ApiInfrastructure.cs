using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qahwa.Core;
using Qahwa.Core.Auth;

namespace Qahwa.Api;

/// <summary>Works out who is making the current request.</summary>
public interface IUserContextResolver
{
    ValueTask<IUserContext> ResolveAsync(HttpContext context, CancellationToken ct = default);
}

/// <summary>
/// Reads the signed session cookie, then re-checks the user against the database (briefly cached), so disabling a
/// user, changing roles or changing the password takes effect on open sessions.
/// </summary>
internal sealed class CookieUserContextResolver(
    AuthService auth,
    ApiKeyService apiKeys,
    IMemoryCache cache,
    IOptions<AuthOptions> options) : IUserContextResolver
{
    private static readonly object ItemKey = new();
    internal static readonly object ViaApiKey = new();

    public async ValueTask<IUserContext> ResolveAsync(HttpContext context, CancellationToken ct = default)
    {
        if (context.Items.TryGetValue(ItemKey, out var cached))
            return (IUserContext)cached!;

        var resolved = await ResolveCoreAsync(context, ct);
        context.Items[ItemKey] = resolved;
        return resolved;
    }

    private async Task<IUserContext> ResolveCoreAsync(HttpContext context, CancellationToken ct)
    {
        // A program acting with an API key: "Authorization: Bearer qk_...". A bad key is simply nobody (401 where a person is needed).
        if (context.Request.Headers.Authorization.ToString() is { } header && header.StartsWith("Bearer qk_", StringComparison.Ordinal))
        {
            var identity = await apiKeys.AuthenticateAsync(header["Bearer ".Length..].Trim(), ct);
            if (identity is null)
                return UserContext.Anonymous;
            if (identity.ReadOnly && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
                throw new PermissionDeniedException("This API key is read-only.");
            context.Items[ViaApiKey] = true;
            return identity.User;
        }

        var principal = context.User;
        if (principal.Identity?.IsAuthenticated != true)
            return UserContext.Anonymous;

        var email = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var version = principal.FindFirstValue(AuthClaims.PasswordVersion);
        if (email is null)
            return UserContext.Anonymous;

        var session = await GetSessionAsync(email, ct);
        return session is not null && session.PasswordVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) == version
            ? session.User
            : UserContext.Anonymous;
    }

    private async Task<SessionInfo?> GetSessionAsync(string email, CancellationToken ct)
    {
        var seconds = options.Value.SessionCacheSeconds;
        if (seconds <= 0)
            return await auth.LoadSessionAsync(email, ct);

        return await cache.GetOrCreateAsync(AuthClaims.SessionKey(email), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(seconds);
            return auth.LoadSessionAsync(email, CancellationToken.None);
        });
    }
}

internal static class AuthClaims
{
    public const string PasswordVersion = "qahwa:pwv";
    public const string LoginRateLimit = "qahwa-login";

    public static string SessionKey(string email) => $"qahwa:session:{email}";
}

public static class ApiServiceCollectionExtensions
{
    public static IServiceCollection AddQahwaApi(this IServiceCollection services)
    {
        services.Configure<JsonOptions>(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddMemoryCache();
        services.AddSingleton<IUserContextResolver, CookieUserContextResolver>();

        // Cross-site request forgery: the cookie is SameSite=Lax (not sent on cross-site POST/PUT/DELETE) and every
        // state-changing endpoint only accepts JSON bodies, which browsers cannot send cross-site without a CORS
        // preflight. Do not enable CORS for untrusted origins.
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<IHostEnvironment, IOptions<AuthOptions>>((cookie, env, auth) =>
            {
                cookie.Cookie.Name = "qahwa.sid";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.SecurePolicy = env.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                cookie.ExpireTimeSpan = TimeSpan.FromHours(auth.Value.SessionHours);
                cookie.SlidingExpiration = true;
                cookie.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
                cookie.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            });

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(WebFormEndpoints.RateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            limiter.AddPolicy(AuthClaims.LoginRateLimit, http =>
            {
                var permits = http.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.LoginPermitsPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(permits, 1),
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
            });
        });

        return services;
    }
}

public static class ApplicationBuilderExtensions
{
    /// <summary>Error envelope, rate limiting and authentication, in the order they must run.</summary>
    public static IApplicationBuilder UseQahwa(this IApplicationBuilder app) =>
        app.UseQahwaErrors().UseRateLimiter().UseAuthentication();

    /// <summary>Maps framework exceptions to the standard <c>{ code, message, details }</c> error envelope.</summary>
    public static IApplicationBuilder UseQahwaErrors(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception ex) when (!context.Response.HasStarted)
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Qahwa.Api");
                var (status, body) = Map(ex, logger);
                context.Response.Clear();
                context.Response.StatusCode = status;
                await context.Response.WriteAsJsonAsync(body);
            }
        });

    private static (int Status, object Body) Map(Exception ex, ILogger logger)
    {
        switch (ex)
        {
            case DocumentValidationException v:
                return (v.StatusCode, new { code = v.Code, message = v.Message, details = v.Errors });
            case QahwaException q:
                return (q.StatusCode, new { code = q.Code, message = q.Message });
            case BadHttpRequestException or JsonException:
                return (400, new { code = "bad_request", message = "The request body or parameters are invalid." });
            default:
                logger.LogError(ex, "Unhandled exception");
                return (500, new { code = "server_error", message = "An unexpected error occurred." });
        }
    }
}
