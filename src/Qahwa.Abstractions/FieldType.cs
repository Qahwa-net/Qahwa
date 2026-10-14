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
}
