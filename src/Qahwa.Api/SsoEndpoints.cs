using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qahwa.Core.Auth;

namespace Qahwa.Api;

/// <summary>Signing in with an identity provider (OpenID Connect).</summary>
public static class SsoEndpoints
{
    private const string CookieName = "qahwa.sso";

    public static IEndpointRouteBuilder MapQahwaSso(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth/sso");

        group.MapGet("", (SsoService sso) => Results.Ok(new { data = sso.Providers().Select(p => new { name = p.Name, display_name = p.DisplayName }) }));

        group.MapGet("/{provider}/start", async (string provider, string? next, HttpContext http, SsoService sso, IOptions<AuthOptions> options, IHostEnvironment env, ILoggerFactory logs, CancellationToken ct) =>
        {
            try
            {
                var redirect = RedirectUri(http, provider, options.Value, env) ?? throw new SsoException("not_configured", "Qahwa:Auth:PublicUrl is not set.");
                var start = await sso.StartAsync(provider, redirect, next, ct);
                http.Response.Cookies.Append(CookieName, start.State, new CookieOptions
                {
                    HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = !env.IsDevelopment() || http.Request.IsHttps, Path = "/api/auth/sso", MaxAge = SsoService.Lifetime, IsEssential = true,
                });
                http.Response.Headers["Cache-Control"] = "no-store";
                return Results.Redirect(start.Url);
            }
            catch (SsoException e)
            {
                logs.CreateLogger("Qahwa.Sso").LogWarning("Single sign-on could not start for {Provider}: {Code} {Detail}", provider, e.Code, e.Message);
                return Results.Redirect("/login?sso_error=" + Uri.EscapeDataString(e.Code is "unknown_provider" or "not_configured" ? e.Code : "provider_error"));
            }
        }).RequireRateLimiting(AuthClaims.LoginRateLimit);

        // The provider sends the browser back here. Whatever goes wrong, the browser is only told a short code, never the reason.
        group.MapGet("/{provider}/callback", async (
            string provider, string? code, string? state, string? error, HttpContext http, SsoService sso, SessionService sessions, AuthService auth,
            IOptions<AuthOptions> options, IHostEnvironment env, ILoggerFactory logs, CancellationToken ct) =>
        {
            var saved = http.Request.Cookies[CookieName];
            http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/api/auth/sso" });
            http.Response.Headers["Cache-Control"] = "no-store";
            try
            {
                if (!string.IsNullOrEmpty(error))
                    throw new SsoException("provider_refused", error.Length > 100 ? error[..100] : error);
                var redirect = RedirectUri(http, provider, options.Value, env) ?? throw new SsoException("not_configured", "No public address.");
                var signIn = await sso.CompleteAsync(provider, redirect, saved, code, state, ct);
                var session = await auth.CompleteLoginAsync(signIn.User, ct) ?? throw new SsoException("disabled", "The account cannot be used.");
                var id = await sessions.StartAsync(session.User.User, http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString(), ct);
                var identity = new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, session.User.User), new Claim(AuthClaims.PasswordVersion, session.PasswordVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)), new Claim(AuthClaims.SessionId, id)],
                    CookieAuthenticationDefaults.AuthenticationScheme);
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
                return Results.Redirect(signIn.Next);
            }
            catch (SsoException e)
            {
                logs.CreateLogger("Qahwa.Sso").LogWarning("Single sign-on failed for {Provider}: {Code} {Detail}", provider, e.Code, e.Message);
                return Results.Redirect("/login?sso_error=" + Uri.EscapeDataString(e.Code));
            }
        }).RequireRateLimiting(AuthClaims.LoginRateLimit);

        return app;
    }

    /// <summary>
    /// Where the provider sends people back to. It comes from the configured public address; only in Development is the request's own
    /// address used (a forged Host header could otherwise steer the sign-in to someone else's site). The provider must have this exact
    /// address registered, which is its own protection against anything else.
    /// </summary>
    private static string? RedirectUri(HttpContext http, string provider, AuthOptions options, IHostEnvironment env)
    {
        if (!SsoService.ValidName(provider))
            return null;
        var baseUrl = options.PublicUrl ?? (env.IsDevelopment() ? $"{http.Request.Scheme}://{http.Request.Host}" : null);
        return baseUrl is null ? null : $"{baseUrl.TrimEnd('/')}/api/auth/sso/{provider}/callback";
    }
}
