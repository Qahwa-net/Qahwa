namespace Qahwa.Abstractions;

/// <summary>Built-in field types a DocType field can use.</summary>
public enum FieldType
{
    Data,
    Text,
    LongText,
    Int,
    Float,
    Decimal,
    Currency,
    Percent,
    Check,
    Select,
    Date,
    Datetime,
    Time,
    Link,
    DynamicLink,
    Table,
    Attach,
    AttachImage,
    Json,
    Password,
    Email,
    Phone,
    Url,
    Code,
    Markdown,
    SectionBreak,
    ColumnBreak,
    TabBreak,

    /// <summary>A whole number of stars from 0 to 5.</summary>
    Rating,

    /// <summary>A length of time as a whole number of seconds.</summary>
    Duration,

    /// <summary>A colour as #rrggbb.</summary>
    Color,

    /// <summary>The text of a barcode or QR code (letters, digits and punctuation), up to 140 characters.</summary>
    Barcode,

    /// <summary>A place on earth as {"lat":..,"lng":..}.</summary>
    Geolocation,

    /// <summary>The name of an icon from the Desk's set (letters and digits, starting with a letter).</summary>
    Icon,

    /// <summary>A drawn signature as a small PNG picture (a data address).</summary>
    Signature,

    /// <summary>A few lines of plain text (shorter than a Text field is expected to be, up to 5,000 characters).</summary>
    SmallText,

    /// <summary>Short text with suggestions: any value is allowed, the options are only offered.</summary>
    Autocomplete,

    /// <summary>Formatted text (bold, lists, links), stored as HTML that has been cleaned by the server.</summary>
    TextEditor,
}
