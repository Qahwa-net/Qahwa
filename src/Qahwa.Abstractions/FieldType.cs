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

    /// <summary>A drawn signature as a small PNG picture (a data address).</summary>
    Signature,
}
