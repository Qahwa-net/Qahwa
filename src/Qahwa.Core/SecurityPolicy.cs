namespace Qahwa.Core.Auth;

/// <summary>
/// The sign-in and password rules in force (ADR-114): the server configuration sets the floor, and a System Manager can only make the
/// rules <b>stricter</b> in System Settings, never looser, so a wrong entry in the Desk cannot let anyone in or lock anyone out for longer
/// than the configuration allows. A value that is missing or out of range is ignored.
/// </summary>
public sealed class SecurityPolicyService(IMetaRegistry registry, IDocumentRepository repository, Microsoft.Extensions.Options.IOptions<AuthOptions> options)
{
    private async Task<int> SettingAsync(string field, int min, int max, CancellationToken ct)
    {
        if (!registry.TryGet(SystemSettingsService.DocTypeName, out var meta))
            return 0;
        var row = await repository.GetAsync(meta, SystemSettingsService.DocTypeName, ct);
        var value = row?.GetValueOrDefault(field);
        var number = value is null ? 0 : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        return number >= min && number <= max ? number : 0;
    }

    /// <summary>The shortest password allowed: the larger of the configuration and the setting.</summary>
    public async Task<int> MinPasswordLengthAsync(CancellationToken ct = default) =>
        Math.Max(options.Value.MinPasswordLength, await SettingAsync("min_password_length", 8, 64, ct));

    /// <summary>The most days a sign-in can last: the smaller of the configuration and the setting.</summary>
    public async Task<int> AbsoluteSessionDaysAsync(CancellationToken ct = default)
    {
        var configured = Math.Max(options.Value.AbsoluteSessionDays, 1);
        var setting = await SettingAsync("absolute_session_days", 1, 90, ct);
        return setting == 0 ? configured : Math.Min(configured, setting);
    }

    /// <summary>After how many days a password must be changed (0: never): the stricter of the configuration and the setting.</summary>
    public async Task<int> PasswordMaxAgeDaysAsync(CancellationToken ct = default)
    {
        var configured = options.Value.PasswordMaxAgeDays;
        var setting = await SettingAsync("password_max_age_days", 1, 3650, ct);
        return configured <= 0 ? setting : setting == 0 ? configured : Math.Min(configured, setting);
    }
}
