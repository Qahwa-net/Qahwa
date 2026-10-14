using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qahwa.Core;

namespace Qahwa.Api;

public sealed class QahwaApiOptions
{
    /// <summary>
    /// Development-only authentication: trusts the X-Qahwa-User and X-Qahwa-Roles request headers.
    /// Refused outside the Development environment. Replaced by real authentication later in Phase 1.
    /// </summary>
    public bool DevAuthEnabled { get; set; }
}

public interface IUserContextResolver
{
    IUserContext Resolve(HttpContext context);
}

internal sealed class AnonymousUserContextResolver : IUserContextResolver
{
    public IUserContext Resolve(HttpContext context) => UserContext.Anonymous;
}

internal sealed class DevUserContextResolver : IUserContextResolver
{
    public IUserContext Resolve(HttpContext context)
    {
        var user = context.Request.Headers["X-Qahwa-User"].ToString();
        if (string.IsNullOrWhiteSpace(user))
            return UserContext.Anonymous;

        var roles = context.Request.Headers["X-Qahwa-Roles"].ToString()
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (user == UserContext.AdministratorRole)
            roles.Add(UserContext.AdministratorRole);

        return new UserContext(user, roles);
    }
}

public static class ApiServiceCollectionExtensions
{
    public static IServiceCollection AddQahwaApi(this IServiceCollection services)
    {
        services.AddOptions<QahwaApiOptions>();
        services.Configure<JsonOptions>(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddSingleton<IUserContextResolver>(sp =>
        {
            if (!sp.GetRequiredService<IOptions<QahwaApiOptions>>().Value.DevAuthEnabled)
                return new AnonymousUserContextResolver();

            if (!sp.GetRequiredService<IHostEnvironment>().IsDevelopment())
                throw new InvalidOperationException("Qahwa dev authentication can only be enabled in the Development environment.");

            return new DevUserContextResolver();
        });
        return services;
    }
}

public static class ErrorHandlingExtensions
{
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
