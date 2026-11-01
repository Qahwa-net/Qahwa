using Qahwa.Abstractions;

namespace Qahwa.Core.Designer;

/// <summary>Which type changes of a saved field the designer allows (ADR-138). Anything that could not keep every saved value is refused.</summary>
public static class FieldTypeChanges
{
    private static bool IsPlainText(FieldType t) =>
        FieldTypes.IsShortText(t) || t is FieldType.Text or FieldType.LongText or FieldType.SmallText or FieldType.Url
            or FieldType.Code or FieldType.Markdown or FieldType.TextEditor;

    private static bool IsWholeNumber(FieldType t) => t is FieldType.Int or FieldType.Rating or FieldType.Duration;

    private static bool IsNumber(FieldType t) =>
        IsWholeNumber(t) || t is FieldType.Float or FieldType.Decimal or FieldType.Currency or FieldType.Percent;

    private static bool IsMoment(FieldType t) => t is FieldType.Date or FieldType.Datetime or FieldType.Time;

    /// <returns>Null when a field of type <paramref name="from"/> may become <paramref name="to"/>; otherwise why not.</returns>
    public static string? Check(FieldType from, FieldType to)
    {
        if (from == to)
            return null;
        if (!FieldTypes.HasColumn(from) || !FieldTypes.HasColumn(to))
            return $"A {from} field cannot become a {to} field: one of them holds no value of its own.";
        if (from is FieldType.Password || to is FieldType.Password)
            return "A Password field cannot change type (its values are stored as hashes).";
        if (IsPlainText(from) && IsPlainText(to))
            return null;
        if (IsNumber(from) && IsPlainText(to))
            return null;
        if (IsPlainText(from) && (IsNumber(to) || IsMoment(to)))
            return null;
        if (IsMoment(from) && IsPlainText(to))
            return null;
        if (IsNumber(from) && IsNumber(to))
            return IsWholeNumber(to) && !IsWholeNumber(from)
                ? $"A {from} field cannot become a {to} field: the decimals of saved values would be lost."
                : null;
        if (from == FieldType.Date && to == FieldType.Datetime)
            return null;
        if (from == FieldType.Datetime && to == FieldType.Date)
            return "A Datetime field cannot become a Date field: the time of day of saved values would be lost.";
        if ((from == FieldType.Check && to == FieldType.Int) || (from == FieldType.Int && to == FieldType.Check))
            return null;
        return $"A {from} field cannot become a {to} field.";
    }
}
