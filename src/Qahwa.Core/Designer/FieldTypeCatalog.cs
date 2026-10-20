using Qahwa.Abstractions;

namespace Qahwa.Core.Designer;

/// <summary>How the "options" of a field are edited: not at all, as a list of choices, or as a DocType to link to.</summary>
public enum OptionsKind
{
    None,
    Choices,
    DocType,
    Html,
}

public sealed record FieldTypeInfo(
    FieldType Type,
    string Label,
    string Group,
    string Hint,
    OptionsKind Options,
    bool Supported,
    string? UnsupportedReason = null);

/// <summary>The field types the designer offers. The Desk renders its type picker from this list.</summary>
public static class FieldTypeCatalog
{
    private const string Later = "Not supported yet.";

    public static readonly IReadOnlyList<FieldTypeInfo> All =
    [
        new(FieldType.Data, "Data", "Text", "Short text, up to 140 characters", OptionsKind.None, true),
        new(FieldType.Text, "Text", "Text", "A few lines of text", OptionsKind.None, true),
        new(FieldType.SmallText, "Small Text", "Text", "A couple of lines of plain text, up to 5,000 characters", OptionsKind.None, true),
        new(FieldType.TextEditor, "Text Editor", "Text", "Formatted text: bold, italic, lists and links", OptionsKind.None, true),
        new(FieldType.Autocomplete, "Autocomplete", "Text", "Short text that suggests values you list, but allows any", OptionsKind.Choices, true),
        new(FieldType.LongText, "Long Text", "Text", "Long free text", OptionsKind.None, true),
        new(FieldType.Markdown, "Markdown", "Text", "Text written in Markdown", OptionsKind.None, true),
        new(FieldType.Code, "Code", "Text", "Source code or any monospaced text", OptionsKind.None, true),
        new(FieldType.Json, "JSON", "Text", "Structured data, validated as JSON", OptionsKind.None, true),
        new(FieldType.Email, "Email", "Text", "A validated email address", OptionsKind.None, true),
        new(FieldType.Phone, "Phone", "Text", "A phone number", OptionsKind.None, true),
        new(FieldType.Url, "URL", "Text", "A web address", OptionsKind.None, true),
        new(FieldType.Color, "Color", "Text", "A colour, picked from a palette", OptionsKind.None, true),
        new(FieldType.Icon, "Icon", "Text", "An icon chosen from a set", OptionsKind.None, true),
        new(FieldType.Barcode, "Barcode", "Text", "The text of a barcode or QR code", OptionsKind.None, true),
        new(FieldType.Geolocation, "Geolocation", "Text", "A place: latitude and longitude", OptionsKind.None, true),
        new(FieldType.Signature, "Signature", "Text", "A signature drawn with the mouse or a finger", OptionsKind.None, true),

        new(FieldType.Int, "Integer", "Number", "A whole number", OptionsKind.None, true),
        new(FieldType.Float, "Float", "Number", "A decimal number", OptionsKind.None, true),
        new(FieldType.Decimal, "Decimal", "Number", "An exact decimal number", OptionsKind.None, true),
        new(FieldType.Currency, "Currency", "Number", "An amount of money", OptionsKind.None, true),
        new(FieldType.Percent, "Percent", "Number", "A percentage", OptionsKind.None, true),
        new(FieldType.Rating, "Rating", "Number", "Stars from 0 to 5", OptionsKind.None, true),
        new(FieldType.Duration, "Duration", "Number", "A length of time (hours, minutes, seconds)", OptionsKind.None, true),

        new(FieldType.Check, "Checkbox", "Choice", "Yes or no", OptionsKind.None, true),
        new(FieldType.Select, "Select", "Choice", "One choice from a list you define", OptionsKind.Choices, true),
        new(FieldType.Link, "Link", "Choice", "A reference to a document of another DocType", OptionsKind.DocType, true),

        new(FieldType.Date, "Date", "Date and time", "A calendar date", OptionsKind.None, true),
        new(FieldType.Datetime, "Date and Time", "Date and time", "A date with a time", OptionsKind.None, true),
        new(FieldType.Time, "Time", "Date and time", "A time of day", OptionsKind.None, true),

        new(FieldType.SectionBreak, "Section", "Layout", "A heading that starts a new section of the form", OptionsKind.None, true),
        new(FieldType.TabBreak, "Tab", "Layout", "A heading that starts a new tab of the form", OptionsKind.None, true),

        new(FieldType.Heading, "Heading", "Layout", "A line of bold text between fields", OptionsKind.None, true),
        new(FieldType.Html, "HTML", "Layout", "A block of fixed text with simple formatting and links", OptionsKind.Html, true),

        new(FieldType.Table, "Table", "Layout", "Rows of a child DocType, e.g. the items of an order", OptionsKind.DocType, true),
        new(FieldType.DynamicLink, "Dynamic Link", "Advanced", "A link whose target DocType is chosen per document", OptionsKind.None, false, Later),
        new(FieldType.Attach, "Attachment", "Advanced", "An uploaded file (attach it after the document is saved)", OptionsKind.None, true),
        new(FieldType.AttachImage, "Image", "Advanced", "An uploaded picture, shown on the form", OptionsKind.None, true),
        new(FieldType.Password, "Password", "Advanced", "A secret stored encrypted and never shown again", OptionsKind.None, true),
        new(FieldType.ColumnBreak, "Column", "Layout", "Starts the next column of the section; fields after it appear beside the earlier ones", OptionsKind.None, true),
    ];

    public static bool IsSupported(FieldType type) => All.Any(i => i.Type == type && i.Supported);
}
