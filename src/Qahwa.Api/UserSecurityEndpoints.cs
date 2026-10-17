using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Memory;
using Qahwa.Core;
using Qahwa.Core.Auth;

namespace Qahwa.Api;

public sealed record AdminPasswordRequest(string? NewPassword);
public sealed record TestEmailRequest(string? To);

public static class UserSecurityEndpoints
{
    public static bool CanManage(IUserContext user) => user.IsAuthenticated &&
        (user.Roles.Contains(UserContext.AdministratorRole, StringComparer.Ordinal) || user.Roles.Contains("System Manager", StringComparer.Ordinal));

    private static void Require(IUserContext user)
    {
        if (!user.IsAuthenticated) throw new UnauthenticatedException();
        if (!CanManage(user)) throw new PermissionDeniedException("Only a System Manager can manage passwords or outgoing email.");
    }

    public static IEndpointRouteBuilder MapUserSecurity(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/users/{email}/password", async (string email, AdminPasswordRequest body, HttpContext http,
            IUserContextResolver users, AuthService auth, SessionService sessions, PermissionService permissions, AccessLogService audit, IMemoryCache cache, CancellationToken ct) =>
        {
            var actor = await users.ResolveAsync(http, ct);
            Require(actor);
            email = AuthService.Normalize(email) ?? throw new DocumentValidationException("A valid user email is required.");
            
            if (await auth.IsAdministratorUserAsync(email, ct) && !permissions.IsAdministrator(actor))
                throw new PermissionDeniedException("Only an Administrator can change an Administrator's password.");
            await auth.SetPasswordAsync(email, body.NewPassword ?? string.Empty, ct);
            cache.Remove(AuthClaims.SessionKey(email));
            foreach (var session in await sessions.ListAsync(email, ct))
                cache.Remove(AuthClaims.LiveKey(session.Id));
            await sessions.RevokeAllForAsync(email, ct);
            await audit.RecordAsync("Set Password", "User", email, 1, actor, ct);
            return Results.Ok(new { message = "Password changed. All sessions for this user have ended." });
        });

        app.MapPost("/api/users/{email}/reset-password", async (string email, HttpContext http,
            IUserContextResolver users, AuthService auth, PasswordResetService reset, PermissionService permissions,
            EmailAccountService accounts, IEmailSender sender, IOptions<AuthOptions> options, IHostEnvironment env, AccessLogService audit, CancellationToken ct) =>
        {
            var actor = await users.ResolveAsync(http, ct);
            Require(actor);
            email = AuthService.Normalize(email) ?? throw new DocumentValidationException("A valid user email is required.");
            
            if (await auth.IsAdministratorUserAsync(email, ct) && !permissions.IsAdministrator(actor))
                throw new PermissionDeniedException("Only an Administrator can reset an Administrator's password.");
            if (!await accounts.IsConfiguredAsync(ct)) throw new DocumentValidationException("Configure and enable Email Account before sending a password-reset link.");
            var baseUrl = options.Value.PublicUrl ?? await accounts.PublicUrlAsync(ct) ?? (env.IsDevelopment() ? $"{http.Request.Scheme}://{http.Request.Host}" : null);
            if (baseUrl is null) throw new DocumentValidationException("Set the public site URL in Email Account before sending password-reset links.");
            var mail = await reset.PrepareAsync(email, baseUrl, ct);
            if (mail is null) throw new DocumentValidationException("The user is disabled, does not exist, or has reached the limit of three reset links per hour.");
            try { await sender.SendAsync(new EmailMessage(mail.To, mail.Subject, mail.Body), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { throw new DocumentValidationException("Could not send the reset email. Check the Email Account connection and credentials."); }
            await audit.RecordAsync("Send Password Reset", "User", email, 1, actor, ct);
            return Results.Ok(new { message = "Password-reset email sent. The link expires in one hour." });
        });

        app.MapPost("/api/email/test", async (TestEmailRequest body, HttpContext http, IUserContextResolver users,
            EmailAccountService accounts, IEmailSender sender, CancellationToken ct) =>
        {
            Require(await users.ResolveAsync(http, ct));
            if (!EmailService.ValidAddress(body.To)) throw new DocumentValidationException("Enter a valid recipient email address.");
            if (!await accounts.IsConfiguredAsync(ct)) throw new DocumentValidationException("Save and enable Email Account before sending a test email.");
            try { await sender.SendAsync(new EmailMessage(body.To!, "Qahwa test email", "Your Qahwa outgoing email account is working."), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { throw new DocumentValidationException("SMTP delivery failed. Check the server, port, STARTTLS setting and credentials."); }
            return Results.Ok(new { message = "Test email sent." });
        });
        return app;
    }
}

