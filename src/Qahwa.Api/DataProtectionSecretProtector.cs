using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Qahwa.Core.Auth;

namespace Qahwa.Api;

/// <summary>
/// Protects two-factor secrets and sign-in challenges with ASP.NET Core Data Protection (authenticated encryption). The keys are kept
/// by Data Protection: with several servers, share its key ring (see the deployment notes), or secrets made on one cannot be read on another.
/// </summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Qahwa.Secrets.v1");

    public string Protect(string plain) => _protector.Protect(plain);

    public string? Unprotect(string protectedText)
    {
        try
        {
            return _protector.Unprotect(protectedText);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
