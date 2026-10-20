using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

public sealed record OAuthDecision(string? ClientId, string? RedirectUri, string? Scope, string? State, string? CodeChallenge, string? CodeChallengeMethod, bool Approve);

/// <summary>Qahwa as an OAuth 2.0 provider (ADR-094): the person's approval, the token endpoint, revocation, user info and client management.</summary>
public static class OAuthEndpoints
{
    private static readonly string[] AllScopes = [OAuthService.ScopeProfile, OAuthService.ScopeApi, OAuthService.ScopeWrite];

    public static IEndpointRouteBuilder MapQahwaOAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/oauth");

        // What other programs need to know to use this server (RFC 8414).
        group.MapGet("/metadata", (HttpContext http) =>
        {
            var origin = $"{http.Request.Scheme}://{http.Request.Host}";
            return Results.Ok(new
            {
                issuer = origin,
                authorization_endpoint = $"{origin}/oauth/authorize",
                token_endpoint = $"{origin}/api/oauth/token",
                revocation_endpoint = $"{origin}/api/oauth/revoke",
                userinfo_endpoint = $"{origin}/api/oauth/userinfo",
                scopes_supported = AllScopes,
                response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "refresh_token" },
                code_challenge_methods_supported = new[] { "S256" },
                token_endpoint_auth_methods_supported = new[] { "client_secret_basic", "client_secret_post", "none" },
            });
        });

        // The consent page asks what is being requested; only a signed-in person (not a program) is asked.
        group.MapGet("/authorize", async (string? client_id, string? redirect_uri, string? scope, string? state, string? code_challenge, string? code_challenge_method,
            HttpContext http, IUserContextResolver users, OAuthService oauth, CancellationToken ct) =>
        {
            RequirePerson(await users.ResolveAsync(http, ct), http);
            try
            {
                var consent = await oauth.ConsentAsync(new OAuthAuthorizeRequest(client_id, redirect_uri, scope, state, code_challenge, code_challenge_method), ct);
                return Results.Ok(new { data = consent });
            }
            catch (OAuthException e)
            {
                return Results.Json(new { code = e.Error, message = e.Message }, statusCode: 400);
            }
        });

        group.MapPost("/authorize", async (OAuthDecision body, HttpContext http, IUserContextResolver users, OAuthService oauth, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            RequirePerson(user, http);
            try
            {
                var redirect = await oauth.DecideAsync(new OAuthAuthorizeRequest(body.ClientId, body.RedirectUri, body.Scope, body.State, body.CodeChallenge, body.CodeChallengeMethod), body.Approve, user, ct);
                return Results.Ok(new { data = new { redirect } });
            }
            catch (OAuthException e)
            {
                return Results.Json(new { code = e.Error, message = e.Message }, statusCode: 400);
            }
        });

        group.MapPost("/token", async (HttpContext http, OAuthService oauth, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.Pragma = "no-cache";
            try
            {
                var form = await ReadFormAsync(http, ct);
                var (id, secret) = ClientCredentials(http, form);
                var token = await oauth.TokenAsync(form.GetValueOrDefault("grant_type"), id, secret, form.GetValueOrDefault("code"), form.GetValueOrDefault("redirect_uri"),
                    form.GetValueOrDefault("code_verifier"), form.GetValueOrDefault("refresh_token"), ct);
                return Results.Json(new { access_token = token.AccessToken, token_type = token.TokenType, expires_in = token.ExpiresIn, refresh_token = token.RefreshToken, scope = token.Scope });
            }
            catch (OAuthException e)
            {
                return Failure(http, e);
            }
        });

        group.MapPost("/revoke", async (HttpContext http, OAuthService oauth, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                var form = await ReadFormAsync(http, ct);
                var (id, secret) = ClientCredentials(http, form);
                await oauth.RevokeAsync(id, secret, form.GetValueOrDefault("token"), ct);
                return Results.Ok();
            }
            catch (OAuthException e)
            {
                return Failure(http, e);
            }
        });

        group.MapGet("/userinfo", async (HttpContext http, OAuthService oauth, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var header = http.Request.Headers.Authorization.ToString();
            var identity = header.StartsWith("Bearer ", StringComparison.Ordinal) ? await oauth.AuthenticateAsync(header["Bearer ".Length..].Trim(), ct) : null;
            if (identity is null)
            {
                http.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
                return Results.Json(new { error = "invalid_token" }, statusCode: 401);
            }

            try
            {
                return Results.Json(await oauth.UserInfoAsync(identity, ct));
            }
            catch (OAuthException e)
            {
                return Failure(http, e);
            }
        });

        // What I have allowed, and taking it back.
        group.MapGet("/connected", async (HttpContext http, IUserContextResolver users, OAuthService oauth, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            RequirePerson(user, http);
            return Results.Ok(new { data = await oauth.ConnectedAsync(user, ct) });
        });

        group.MapDelete("/connected/{clientId}", async (string clientId, HttpContext http, IUserContextResolver users, OAuthService oauth, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            RequirePerson(user, http);
            await oauth.DisconnectAsync(clientId, user, ct);
            return Results.Ok(new { message = "ok" });
        });

        // The registered applications (System Managers).
        group.MapGet("/clients", async (HttpContext http, IUserContextResolver users, OAuthService oauth, CancellationToken ct) =>
            Results.Ok(new { data = await oauth.ListClientsAsync(await users.ResolveAsync(http, ct), ct) }));

        group.MapPost("/clients", async (OAuthClientRequest body, HttpContext http, IUserContextResolver users, OAuthService oauth, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            RequirePerson(user, http);
            var created = await oauth.CreateClientAsync(body, user, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Created($"/api/oauth/clients/{created.Info.Id}", new { data = created });
        });

        group.MapDelete("/clients/{id}", async (string id, HttpContext http, IUserContextResolver users, OAuthService oauth, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            RequirePerson(user, http);
            await oauth.DeleteClientAsync(id, user, ct);
            return Results.Ok(new { message = "ok" });
        });

        return app;
    }

    /// <summary>Approving, listing and managing are for a person at the Desk, not for a program holding a key or a token.</summary>
    private static void RequirePerson(IUserContext user, HttpContext http)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (http.Items.ContainsKey(CookieUserContextResolver.ViaApiKey))
            throw new PermissionDeniedException("This needs you to be signed in, not an API key or an access token.");
    }

    private static async Task<Dictionary<string, string>> ReadFormAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.ContentLength is > 16 * 1024 || !(http.Request.ContentType ?? string.Empty).StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            throw new OAuthException("invalid_request", "Send the request as application/x-www-form-urlencoded (at most 16 KB).");
        var form = await http.Request.ReadFormAsync(ct);
        return form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);
    }

    /// <summary>The client's name and secret from the Authorization header (Basic) or the form.</summary>
    private static (string? Id, string? Secret) ClientCredentials(HttpContext http, Dictionary<string, string> form)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var pair = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var colon = pair.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                    return (Uri.UnescapeDataString(pair[..colon]), Uri.UnescapeDataString(pair[(colon + 1)..]));
            }
            catch (FormatException)
            {
                // fall through to the form
            }
        }

        return (form.GetValueOrDefault("client_id"), form.GetValueOrDefault("client_secret"));
    }

    private static IResult Failure(HttpContext http, OAuthException e)
    {
        if (e.Status == 401)
            http.Response.Headers.WWWAuthenticate = "Basic realm=\"oauth\"";
        return Results.Json(new { error = e.Error, error_description = e.Message }, statusCode: e.Status);
    }
}
