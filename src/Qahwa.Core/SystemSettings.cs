using System.Net;
using System.Net.Sockets;

namespace Qahwa.Core;

/// <summary>How dates and weeks look for everyone. Safe to show to any signed-in person.</summary>
public sealed record DisplaySettings(string TimeZone, string DateFormat, string FirstDayOfWeek, string DefaultLanguage);

/// <summary>
/// The display part of System Settings (a single DocType edited by System Managers). Reading never fails: a value that is not valid
/// (a time zone that does not exist, a language code with symbols in it) falls back to the default instead of breaking every page.
/// </summary>
public sealed class SystemSettingsService(IMetaRegistry registry, IDocumentRepository repository)
{
    public const string DocTypeName = "System Settings";
    public static readonly string[] DateFormats = ["yyyy-mm-dd", "dd/mm/yyyy", "mm/dd/yyyy"];
    public static readonly string[] Weeks = ["Monday", "Sunday", "Saturday"];
    public static readonly DisplaySettings Defaults = new("UTC", "yyyy-mm-dd", "Monday", "en");

    public async Task<DisplaySettings> GetAsync(CancellationToken ct = default)
    {
        var row = await repository.GetAsync(registry.Get(DocTypeName), DocTypeName, ct);
        if (row is null)
            return Defaults;

        var zone = row.GetValueOrDefault("time_zone") as string;
        var format = row.GetValueOrDefault("date_format") as string;
        var week = row.GetValueOrDefault("first_day_of_week") as string;
        var language = row.GetValueOrDefault("default_language") as string;
        return new DisplaySettings(
            IsTimeZone(zone) ? zone! : Defaults.TimeZone,
            format is not null && DateFormats.Contains(format) ? format : Defaults.DateFormat,
            week is not null && Weeks.Contains(week) ? week : Defaults.FirstDayOfWeek,
            TranslationService.IsLanguage(language) ? language! : Defaults.DefaultLanguage);
    }

    public static bool IsTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || !id.All(c => char.IsLetterOrDigit(c) || c is '/' or '_' or '-' or '+'))
            return false;
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}

/// <summary>
/// Which networks may use the system (<c>Qahwa:Auth:AllowedIps</c>, a list of addresses and CIDR ranges such as <c>10.0.0.0/8</c>).
/// An empty list means no restriction. It lives in configuration, not in a setting inside the Desk, on purpose: a wrong entry typed
/// into the Desk could lock every administrator out with no way back except the database.
/// </summary>
public sealed class IpAllowList
{
    private readonly List<(byte[] Network, int Bits)> _ranges = [];

    public static readonly IpAllowList None = new();

    public bool IsEmpty => _ranges.Count == 0;

    /// <exception cref="FormatException">An entry is not an address or a range (so a typo stops the server at start-up instead of quietly opening or closing the system).</exception>
    public static IpAllowList Parse(IEnumerable<string>? entries)
    {
        var list = new IpAllowList();
        foreach (var raw in entries ?? [])
        {
            var entry = raw?.Trim() ?? string.Empty;
            if (entry.Length == 0)
                continue;
            var parts = entry.Split('/');
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address))
                throw new FormatException($"'{entry}' is not an IP address or range (use 203.0.113.5 or 10.0.0.0/8).");

            address = Normalize(address);
            var bytes = address.GetAddressBytes();
            var bits = bytes.Length * 8;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out bits) || bits < 0 || bits > bytes.Length * 8))
                throw new FormatException($"'{entry}' has a prefix length that is out of range.");
            list._ranges.Add((bytes, bits));
        }

        return list;
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>True when there is no restriction, or the address is inside a range. An unknown address is never allowed once a list exists.</summary>
    public bool Allows(IPAddress? address)
    {
        if (IsEmpty)
            return true;
        if (address is null)
            return false;

        var bytes = Normalize(address).GetAddressBytes();
        foreach (var (network, bits) in _ranges)
        {
            if (network.Length != bytes.Length)
                continue;
            var whole = bits / 8;
            var rest = bits % 8;
            var same = network.AsSpan(0, whole).SequenceEqual(bytes.AsSpan(0, whole));
            if (same && (rest == 0 || ((network[whole] ^ bytes[whole]) & (0xFF << (8 - rest))) == 0))
                return true;
        }

        return false;
    }
}
