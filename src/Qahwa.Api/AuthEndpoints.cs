using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Qahwa.Core;
using Qahwa.Core.Auth;

namespace Qahwa.Api;

public sealed record LoginRequest(string? Email, string? Password);

public sealed record ChangePasswordRequest(
    [property: JsonPropertyName("old_password")] string? OldPassword,
    [property: JsonPropertyName("new_password")] string? NewPassword);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapQahwaAuth(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapPost("/login", async (LoginRequest body, HttpContext http, AuthService service, CancellationToken ct) =>
        {
            var session = await service.LoginAsync(body.Email, body.Password, ct)
                ?? throw new InvalidCredentialsException();

            await SignInAsync(http, session);
            return Results.Ok(new { data = Describe(session) });
        }).RequireRateLimiting(AuthClaims.LoginRateLimit);

        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { message = "ok" });
        });

        auth.MapGet("/me", async (HttpContext http, IUserContextResolver users, AuthService service, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            if (!user.IsAuthenticated)
                throw new UnauthenticatedException();

            var session = await service.LoadSessionAsync(user.User, ct) ?? throw new UnauthenticatedException();
            return Results.Ok(new { data = Describe(session) });
        });

        auth.MapPost("/change-password", async (
            ChangePasswordRequest body, HttpContext http, IUserContextResolver users, AuthService service,
            IMemoryCache cache, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            if (!user.IsAuthenticated)
                throw new UnauthenticatedException();

            await service.ChangePasswordAsync(user.User, body.OldPassword ?? string.Empty, body.NewPassword ?? string.Empty, ct);
            cache.Remove(AuthClaims.SessionKey(user.User));

            // Changing the password ends every other session; keep this one alive by re-issuing its cookie.
            var session = await service.LoadSessionAsync(user.User, ct) ?? throw new UnauthenticatedException();
            await SignInAsync(http, session);
            return Results.Ok(new { message = "ok" });
        }).RequireRateLimiting(AuthClaims.LoginRateLimit);

        return app;
    }

    private static object Describe(SessionInfo session) =>
        new { user = session.User.User, full_name = session.FullName, roles = session.User.Roles };

    private static Task SignInAsync(HttpContext http, SessionInfo session)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, session.User.User),
                new Claim(AuthClaims.PasswordVersion, session.PasswordVersion.ToString(CultureInfo.InvariantCulture)),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        return http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false });
    }
}
