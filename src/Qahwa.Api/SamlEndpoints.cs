using System.Security;
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

/// <summary>Signing in with a SAML 2.0 provider.</summary>
public static class SamlEndpoints
{
    private const string CookieName = "qahwa.saml";

    public static IEndpointRouteBuilder MapQahwaSaml(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth/saml");

        group.MapGet("", (SamlService saml) => Results.Ok(new { data = saml.Providers().Select(p => new { name = p.Name, display_name = p.DisplayName }) }));

        group.MapGet("/{provider}/login", (string provider, string? next, HttpContext http, SamlService saml, IOptions<SamlOptions> options, IOptions<AuthOptions> auth, IHostEnvironment env, ILoggerFactory logs) =>
        {
            try
            {
                if (!SsoService.ValidName(provider) || !options.Value.Providers.ContainsKey(provider))
                    throw new SsoException("unknown_provider", "No such provider.");
                var (acs, entity) = Addresses(http, provider, options.Value, auth.Value, env) ?? throw new SsoException("not_configured", "Qahwa:Auth:PublicUrl is not set.");
                var start = saml.Start(provider, acs, entity, next);
                // The provider's answer arrives as a form posted from another site, so the cookie must be sent on that cross-site POST (SameSite=None, which browsers only
                // accept with Secure). In Development over plain http that is not possible and Lax is used (fine for a provider on the same machine).
                var secure = !env.IsDevelopment() || http.Request.IsHttps;
                http.Response.Cookies.Append(CookieName, start.State, new CookieOptions
                {
                    HttpOnly = true, SameSite = secure ? SameSiteMode.None : SameSiteMode.Lax, Secure = secure, Path = "/api/auth/saml", MaxAge = SamlService.Lifetime, IsEssential = true,
                });
                http.Response.Headers["Cache-Control"] = "no-store";
                return Results.Redirect(start.Url);
            }
            catch (SsoException e)
            {
                logs.CreateLogger("Qahwa.Saml").LogWarning("SAML sign-in could not start for {Provider}: {Code} {Detail}", provider, e.Code, e.Message);
                return Results.Redirect("/login?sso_error=" + Uri.EscapeDataString(e.Code is "unknown_provider" or "not_configured" ? e.Code : "provider_error"));
            }
        }).RequireRateLimiting(AuthClaims.LoginRateLimit);

        // The provider's answer. Whatever goes wrong, the browser is only told a short code.
        group.MapPost("/{provider}/acs", async (
            string provider, HttpContext http, SamlService saml, SessionService sessions, AuthService auth, IOptions<SamlOptions> options, IOptions<AuthOptions> authOptions,
            IHostEnvironment env, ILoggerFactory logs, CancellationToken ct) =>
        {
            var saved = http.Request.Cookies[CookieName];
            http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/api/auth/saml" });
            http.Response.Headers["Cache-Control"] = "no-store";
            try
            {
                if (!http.Request.HasFormContentType)
                    throw new SsoException("no_response", "Not a form.");
                var form = await http.Request.ReadFormAsync(ct);
                var (acs, entity) = Addresses(http, provider, options.Value, authOptions.Value, env) ?? throw new SsoException("not_configured", "No public address.");
                var signIn = await saml.CompleteAsync(provider, acs, entity, saved, form["SAMLResponse"], form["RelayState"], ct);
                var session = await auth.CompleteLoginAsync(signIn.User, ct) ?? throw new SsoException("disabled", "The account cannot be used.");
                var id = await sessions.StartAsync(session.User.User, http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString(), ct);
                var identity = new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, session.User.User), new Claim(AuthClaims.PasswordVersion, session.PasswordVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)), new Claim(AuthClaims.SessionId, id)],
                    CookieAuthenticationDefaults.AuthenticationScheme);
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
                return Results.Redirect(signIn.Next, permanent: false, preserveMethod: false); // 302 turns the POST into a GET
            }
            catch (SsoException e)
            {
                logs.CreateLogger("Qahwa.Saml").LogWarning("SAML sign-in failed for {Provider}: {Code} {Detail}", provider, e.Code, e.Message);
                return Results.Redirect("/login?sso_error=" + Uri.EscapeDataString(e.Code));
            }
        }).RequireRateLimiting(AuthClaims.LoginRateLimit);

        // What the provider's administrator needs to register this application.
        group.MapGet("/{provider}/metadata", (string provider, HttpContext http, IOptions<SamlOptions> options, IOptions<AuthOptions> auth, IHostEnvironment env) =>
        {
            if (Addresses(http, provider, options.Value, auth.Value, env) is not var (acs, entity))
                return Results.NotFound();
            var xml = "<?xml version=\"1.0\"?><md:EntityDescriptor xmlns:md=\"urn:oasis:names:tc:SAML:2.0:metadata\" entityID=\"" + SecurityElement.Escape(entity) + "\">" +
                      "<md:SPSSODescriptor AuthnRequestsSigned=\"false\" WantAssertionsSigned=\"true\" protocolSupportEnumeration=\"urn:oasis:names:tc:SAML:2.0:protocol\">" +
                      "<md:NameIDFormat>urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress</md:NameIDFormat>" +
                      "<md:AssertionConsumerService Binding=\"urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST\" Location=\"" + SecurityElement.Escape(acs) + "\" index=\"0\" isDefault=\"true\"/>" +
                      "</md:SPSSODescriptor></md:EntityDescriptor>";
            return Results.Content(xml, "application/samlmetadata+xml");
        });

        return app;
    }

    /// <summary>
    /// This application's address for a provider (where the answer is posted) and its entity id, from the configured public address
    /// (only in Development the request's own address), never from the Host header in production.
    /// </summary>
    private static (string Acs, string Entity)? Addresses(HttpContext http, string provider, SamlOptions options, AuthOptions auth, IHostEnvironment env)
    {
        if (!SsoService.ValidName(provider) || !options.Providers.TryGetValue(provider, out var p))
            return null;
        var baseUrl = auth.PublicUrl ?? (env.IsDevelopment() ? $"{http.Request.Scheme}://{http.Request.Host}" : null);
        return baseUrl is null ? null : ($"{baseUrl.TrimEnd('/')}/api/auth/saml/{provider}/acs", SamlService.EntityId(provider, baseUrl, p));
    }
}
