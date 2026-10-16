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

public sealed record TwoFactorLoginRequest(string? Token, string? Code);

public sealed record TwoFactorCodeRequest(string? Code, string? Password);

public sealed record TwoFactorResetRequest(string? User);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapQahwaAuth(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapPost("/login", async (LoginRequest body, HttpContext http, AuthService service, TwoFactorService twoFactor, SessionService sessions, CancellationToken ct) =>
        {
            var key = await service.VerifyPasswordAsync(body.Email, body.Password, ct)
                ?? throw new InvalidCredentialsException();

            // With two-factor on, the password alone gets no session: only a short-lived challenge to answer with a code.
            if (await twoFactor.IsEnabledAsync(key, ct))
                return Results.Ok(new { data = new { two_factor = true, token = twoFactor.CreateChallenge(key) } });

            var session = await service.CompleteLoginAsync(key, ct) ?? throw new InvalidCredentialsException();
            await SignInAsync(http, session, await StartSessionAsync(http, sessions, session.User.User, ct));
            return Results.Ok(new { data = Describe(session) });
        }).RequireRateLimiting(AuthClaims.LoginRateLimit);

        auth.MapPost("/login/2fa", async (TwoFactorLoginRequest body, HttpContext http, AuthService service, TwoFactorService twoFactor, SessionService sessions, CancellationToken ct) =>
        {
            var key = twoFactor.RedeemChallenge(body.Token) ?? throw new InvalidCredentialsException();
            if (!await twoFactor.VerifyLoginAsync(key, body.Code, ct))
                throw new InvalidCodeException();

            var session = await service.CompleteLoginAsync(key, ct) ?? throw new InvalidCredentialsException();
            await SignInAsync(http, session, await StartSessionAsync(http, sessions, session.User.User, ct));
            return Results.Ok(new { data = Describe(session) });
        }).RequireRateLimiting(AuthClaims.LoginRateLimit);

        // The person's own sign-ins: see where they are signed in and end any of them.
        var mine = auth.MapGroup("/sessions");

        mine.MapGet("", async (HttpContext http, IUserContextResolver users, SessionService sessions, CancellationToken ct) =>
        {
            var user = await PersonAsync(http, users, ct);
            var current = http.User.FindFirst(AuthClaims.SessionId)?.Value;
            return Results.Ok(new
            {
                data = (await sessions.ListAsync(user.User, ct)).Select(s => new { id = s.Id, created = s.Created, last_seen = s.LastSeen, address = s.Address, agent = s.Agent, current = s.Id == current }),
            });
        });

        mine.MapDelete("/{id}", async (string id, HttpContext http, IUserContextResolver users, SessionService sessions, IMemoryCache cache, CancellationToken ct) =>
        {
            var user = await PersonAsync(http, users, ct);
            if (!await sessions.RevokeAsync(user.User, id, ct))
                throw new NotFoundException("That sign-in was not found.");
            cache.Remove(AuthClaims.LiveKey(id));
            return Results.Ok(new { message = "ok" });
        });

        mine.MapPost("/revoke-others", async (HttpContext http, IUserContextResolver users, SessionService sessions, IMemoryCache cache, CancellationToken ct) =>
        {
            var user = await PersonAsync(http, users, ct);
            var current = http.User.FindFirst(AuthClaims.SessionId)?.Value ?? string.Empty;
            foreach (var other in await sessions.ListAsync(user.User, ct))
                if (other.Id != current)
                    cache.Remove(AuthClaims.LiveKey(other.Id));
            return Results.Ok(new { data = new { revoked = await sessions.RevokeOthersAsync(user.User, current, ct) } });
        });

        // An administrator can sign someone out everywhere (a lost laptop, a leaver).
        mine.MapPost("/revoke-user", async (TwoFactorResetRequest body, HttpContext http, IUserContextResolver users, PermissionService permissions, SessionService sessions, IMemoryCache cache, CancellationToken ct) =>
        {
            var user = await PersonAsync(http, users, ct);
            if (!permissions.IsAdministrator(user) && !user.Roles.Contains("System Manager", StringComparer.Ordinal))
                throw new PermissionDeniedException("Only a System Manager can sign someone out.");
            var target = AuthService.Normalize(body.User) ?? throw new DocumentValidationException([new FieldError("user", "Choose a user.")]);
            foreach (var live in await sessions.ListAsync(target, ct))
                cache.Remove(AuthClaims.LiveKey(live.Id));
            return Results.Ok(new { data = new { revoked = await sessions.RevokeAllForAsync(target, ct) } });
        });

        // Two-factor settings belong to the person at the keyboard: an API key can neither look at nor change them.
        var second = auth.MapGroup("/2fa").RequireRateLimiting(AuthClaims.LoginRateLimit);

        second.MapGet("", async (HttpContext http, IUserContextResolver users, TwoFactorService twoFactor, CancellationToken ct) =>
        {
            var user = await PersonAsync(http, users, ct);
            return Results.Ok(new { data = await twoFactor.StatusAsync(user.User, ct) });
        });

        second.MapPost("/setup", async (HttpContext http, IUserContextResolver users, TwoFactorService twoFactor, CancellationToken ct) =>
        {
            var user = await PersonAsync(http, users, ct);
            return Results.Ok(new { data = await twoFactor.BeginSetupAsync(user.User, "Qahwa", ct) });
        });

        second.MapPost("/enable", async (TwoFactorCodeRequest body, HttpContext http, IUserContextResolver users, TwoFactorService twoFactor, CancellationToken ct) =>
        {
            var user = await PersonAsync(http, users, ct);
            return Results.Ok(new { data = new { recovery_codes = await twoFactor.EnableAsync(user.User, body.Code, ct) } });
        });

        second.MapPost("/disable", async (TwoFactorCodeRequest body, HttpContext http, IUserContextResolver users, AuthService service, TwoFactorService twoFactor, CancellationToken ct) =>
        {
            var user = await PersonAsync(http, users, ct);
            if (!await service.CheckPasswordAsync(user.User, body.Password, ct))
                throw new DocumentValidationException([new FieldError("password", "The password is incorrect.")]);
            await twoFactor.DisableAsync(user.User, body.Code, ct);
            return Results.Ok(new { message = "ok" });
        });

        second.MapPost("/reset", async (TwoFactorResetRequest body, HttpContext http, IUserContextResolver users, PermissionService permissions, TwoFactorService twoFactor, CancellationToken ct) =>
        {
            var user = await PersonAsync(http, users, ct);
            if (!permissions.IsAdministrator(user) && !user.Roles.Contains("System Manager", StringComparer.Ordinal))
                throw new PermissionDeniedException("Only a System Manager can reset someone's two-factor sign-in.");
            var target = AuthService.Normalize(body.User) ?? throw new DocumentValidationException([new FieldError("user", "Choose a user.")]);
            await twoFactor.AdminResetAsync(target, ct);
            return Results.Ok(new { message = "ok" });
        });

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
            IMemoryCache cache, SessionService sessions, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            if (!user.IsAuthenticated)
                throw new UnauthenticatedException();

            await service.ChangePasswordAsync(user.User, body.OldPassword ?? string.Empty, body.NewPassword ?? string.Empty, ct);
            cache.Remove(AuthClaims.SessionKey(user.User));

            // Changing the password ends every other session; keep this one alive by re-issuing its cookie.
            var session = await service.LoadSessionAsync(user.User, ct) ?? throw new UnauthenticatedException();
            var current = http.User.FindFirst(AuthClaims.SessionId)?.Value ?? await StartSessionAsync(http, sessions, user.User, ct);
            await sessions.RevokeOthersAsync(user.User, current, ct);
            await SignInAsync(http, session, current);
            return Results.Ok(new { message = "ok" });
        }).RequireRateLimiting(AuthClaims.LoginRateLimit);

        return app;
    }

    private static async Task<IUserContext> PersonAsync(HttpContext http, IUserContextResolver users, CancellationToken ct)
    {
        var user = await users.ResolveAsync(http, ct); // resolving is what notices an API key
        if (http.Items.ContainsKey(CookieUserContextResolver.ViaApiKey))
            throw new PermissionDeniedException("Two-factor settings cannot be used with an API key. Sign in to change them.");
        return user.IsAuthenticated ? user : throw new UnauthenticatedException();
    }

    private static object Describe(SessionInfo session) =>
        new { user = session.User.User, full_name = session.FullName, roles = session.User.Roles };

    private static Task<string> StartSessionAsync(HttpContext http, SessionService sessions, string user, CancellationToken ct) =>
        sessions.StartAsync(user, http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString(), ct);

    private static async Task SignInAsync(HttpContext http, SessionInfo session, string sessionId)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, session.User.User),
                new Claim(AuthClaims.PasswordVersion, session.PasswordVersion.ToString(CultureInfo.InvariantCulture)),
                new Claim(AuthClaims.SessionId, sessionId),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false });
    }
}
