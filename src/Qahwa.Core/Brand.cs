using System.Text.RegularExpressions;

namespace Qahwa.Core;

/// <summary>How this installation presents itself: its name, logo, colours and default theme (the Brand Settings DocType).</summary>
public sealed record Brand(string Name, string? Logo, string? Color, string? ColorDark, string Theme, string? LoginMessage);

/// <summary>
/// Branding. Everything here ends up on pages that anyone can open (the sign-in page), so it is checked when saved and checked again
/// when read: a name and message are plain text of limited length, colours are <c>#rrggbb</c>, and the logo is a small embedded
/// picture whose bytes must really be the picture type it claims (and an SVG must carry no script or external reference), so the
/// setting can never carry markup, a link to another site or an oversized file.
/// </summary>
public static partial class BrandService
{
    public const string DocTypeName = "Brand Settings";
    public const string DefaultName = "Qahwa";
    public const int MaxNameLength = 60;
    public const int MaxMessageLength = 300;
    public const int MaxLogoBytes = 100 * 1024;

    public static readonly Brand Default = new(DefaultName, null, null, null, "auto", null);

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorRule();

    [GeneratedRegex("^data:image/(png|jpeg|gif|webp|svg\\+xml);base64,([A-Za-z0-9+/]+={0,2})$")]
    private static partial Regex LogoRule();

    [GeneratedRegex("<\\s*(script|foreignObject|iframe|object|embed|image|use|a)\\b|\\bon[a-z]+\\s*=|javascript:|data:text|<!ENTITY|<!DOCTYPE|xlink:href|href\\s*=|url\\s*\\(\\s*['\"]?\\s*(https?:|//)", RegexOptions.IgnoreCase)]
    private static partial Regex ActiveSvg();

    /// <summary>The brand as it is stored, with anything unusable replaced by the default (so a bad row can never break the sign-in page).</summary>
    public static async Task<Brand> GetAsync(IMetaRegistry registry, IDocumentRepository repository, CancellationToken ct = default)
    {
        var row = await repository.GetAsync(registry.Get(DocTypeName), DocTypeName, ct);
        return row is null ? Default : From(row);
    }

    public static Brand From(IReadOnlyDictionary<string, object?> row)
    {
        string? Text(string field) => row.GetValueOrDefault(field) as string is { Length: > 0 } s ? s : null;
        var name = Text("brand_name")?.Trim();
        var message = Text("login_message")?.Trim();
        var logo = Text("brand_logo");
        return new Brand(
            name is { Length: > 0 } && name.Length <= MaxNameLength && PlainText(name, allowNewline: false) ? name : DefaultName,
            logo is not null && LogoProblem(logo) is null ? logo : null,
            Text("brand_color") is { } c && ColorRule().IsMatch(c) ? c.ToLowerInvariant() : null,
            Text("brand_color_dark") is { } d && ColorRule().IsMatch(d) ? d.ToLowerInvariant() : null,
            Text("default_theme")?.ToLowerInvariant() is "light" or "dark" ? Text("default_theme")!.ToLowerInvariant() : "auto",
            message is { Length: > 0 } && message.Length <= MaxMessageLength && PlainText(message, allowNewline: true) ? message : null);
    }

    private static bool PlainText(string text, bool allowNewline) =>
        text.All(c => !char.IsControl(c) || (allowNewline && c is '\n' or '\r')) && !text.Contains('<') && !text.Contains('>');

    /// <summary>What is wrong with a logo, or null when it is a usable small picture.</summary>
    public static string? LogoProblem(string logo)
    {
        if (logo.Length > MaxLogoBytes * 4 / 3 + 64)
            return $"The logo is too large (at most {MaxLogoBytes / 1024} KB).";
        var match = LogoRule().Match(logo);
        if (!match.Success)
            return "The logo must be a PNG, JPEG, GIF, WebP or SVG picture embedded as a data: address.";
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(match.Groups[2].Value);
        }
        catch (FormatException)
        {
            return "The logo is not valid.";
        }

        if (bytes.Length is 0 or > MaxLogoBytes)
            return $"The logo is too large (at most {MaxLogoBytes / 1024} KB).";
        var type = match.Groups[1].Value;
        var ok = type switch
        {
            "png" => bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
            "jpeg" => bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
            "gif" => bytes.AsSpan().StartsWith("GIF8"u8),
            "webp" => bytes.Length > 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => Svg(bytes),
        };
        return ok ? null : "The picture does not match its type, or it contains something active.";
    }

    private static bool Svg(byte[] bytes)
    {
        string text;
        try
        {
            text = new System.Text.UTF8Encoding(false, true).GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return false;
        }

        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        return (trimmed.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            && trimmed.Contains("<svg", StringComparison.OrdinalIgnoreCase) && !ActiveSvg().IsMatch(text);
    }

    /// <summary>Checks the values of a Brand Settings document being saved.</summary>
    public static void Check(IReadOnlyDictionary<string, object?> values, List<FieldError> errors)
    {
        string? Text(string field) => values.GetValueOrDefault(field) as string is { Length: > 0 } s ? s : null;
        if (Text("brand_name")?.Trim() is { } name && (name.Length > MaxNameLength || !PlainText(name, allowNewline: false)))
            errors.Add(new FieldError("brand_name", $"The name is plain text of at most {MaxNameLength} characters."));
        if (Text("login_message")?.Trim() is { } message && (message.Length > MaxMessageLength || !PlainText(message, allowNewline: true)))
            errors.Add(new FieldError("login_message", $"The message is plain text of at most {MaxMessageLength} characters, without < or >."));
        if (Text("brand_logo") is { } logo && LogoProblem(logo) is { } problem)
            errors.Add(new FieldError("brand_logo", problem));
    }
}
