using Qahwa.Abstractions;

namespace Qahwa.Core;

public static class FieldTypes
{
    public static bool IsLayout(FieldType type) =>
        type is FieldType.SectionBreak or FieldType.ColumnBreak or FieldType.TabBreak;

    /// <summary>True when the field is stored as a column on the document's own table.</summary>
    public static bool HasColumn(FieldType type) => !IsLayout(type) && type != FieldType.Table;

    /// <summary>Types stored as short strings (varchar 140), same limit as document names.</summary>
    public static bool IsShortText(FieldType type) =>
        type is FieldType.Data or FieldType.Email or FieldType.Phone or FieldType.Select or FieldType.Autocomplete
            or FieldType.Link or FieldType.DynamicLink or FieldType.Color or FieldType.Barcode or FieldType.Icon;

    /// <summary>Types where an empty string is a legitimate value rather than "no value".</summary>
    public static bool IsTextual(FieldType type) =>
        IsShortText(type) || type is FieldType.Text or FieldType.LongText or FieldType.Attach
            or FieldType.AttachImage or FieldType.Json or FieldType.Url or FieldType.Code
            or FieldType.Markdown or FieldType.Password or FieldType.SmallText or FieldType.TextEditor;

    public const int ShortTextLength = 140;
}

/// <summary>Columns every table has, independent of the DocType's own fields.</summary>
public static class StandardFields
{
    public static readonly IReadOnlyDictionary<string, FieldType> Columns = new Dictionary<string, FieldType>
    {
        ["name"] = FieldType.Data,
        ["creation"] = FieldType.Datetime,
        ["modified"] = FieldType.Datetime,
        ["modified_by"] = FieldType.Data,
        ["owner"] = FieldType.Data,
        ["docstatus"] = FieldType.Int,
        ["idx"] = FieldType.Int,
    };

    public static readonly IReadOnlyDictionary<string, FieldType> ChildColumns = new Dictionary<string, FieldType>
    {
        ["parent"] = FieldType.Data,
        ["parenttype"] = FieldType.Data,
        ["parentfield"] = FieldType.Data,
    };

    public static bool IsReserved(string name) => Columns.ContainsKey(name) || ChildColumns.ContainsKey(name);
}
