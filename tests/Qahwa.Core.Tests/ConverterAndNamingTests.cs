using System.Text.Json;
using Qahwa.Abstractions;

namespace Qahwa.Core.Tests;

public class ConverterAndNamingTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Theory]
    [InlineData("12", 12)]
    [InlineData("\"7\"", 7)]
    public void Int_accepts_whole_numbers(string raw, int expected) =>
        Assert.Equal(expected, FieldValueConverter.ToStorage(FieldType.Int, Json(raw)));

    [Theory]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("\"abc\"")]
    [InlineData("99999999999")]
    public void Int_rejects_everything_else(string raw) =>
        Assert.Throws<FormatException>(() => FieldValueConverter.ToStorage(FieldType.Int, Json(raw)));

    [Fact]
    public void Empty_string_is_null_for_non_text_types_but_kept_for_text()
    {
        Assert.Null(FieldValueConverter.ToStorage(FieldType.Int, ""));
        Assert.Null(FieldValueConverter.ToStorage(FieldType.Date, ""));
        Assert.Equal("", FieldValueConverter.ToStorage(FieldType.Data, ""));
    }

    [Fact]
    public void Currency_rejects_values_too_large_for_the_column() =>
        Assert.Throws<FormatException>(() => FieldValueConverter.ToStorage(FieldType.Currency, 1e13m));

    [Theory]
    [InlineData("true", true)]
    [InlineData("0", false)]
    public void Check_accepts_common_spellings(string raw, bool expected) =>
        Assert.Equal(expected, FieldValueConverter.ToStorage(FieldType.Check, raw));

    [Fact]
    public void Date_and_datetime_normalize_to_utc_and_microseconds()
    {
        Assert.Equal(new DateOnly(2026, 3, 9), FieldValueConverter.ToStorage(FieldType.Date, "2026-03-09"));

        var stored = (DateTime)FieldValueConverter.ToStorage(FieldType.Datetime, "2026-03-09T10:00:00.1234567+02:00")!;
        Assert.Equal(DateTimeKind.Utc, stored.Kind);
        Assert.Equal(8, stored.Hour);
        Assert.Equal(0, stored.Ticks % 10);
    }

    [Fact]
    public void Email_url_and_json_are_validated()
    {
        Assert.Throws<FormatException>(() => FieldValueConverter.ToStorage(FieldType.Email, "not-an-email"));
        Assert.Throws<FormatException>(() => FieldValueConverter.ToStorage(FieldType.Url, "/relative"));
        Assert.ThrowsAny<Exception>(() => FieldValueConverter.ToStorage(FieldType.Json, "{ nope"));
        Assert.Equal("{\"a\":1}", FieldValueConverter.ToStorage(FieldType.Json, "{\"a\":1}"));
    }

    [Fact]
    public void Password_fields_take_text_up_to_a_limit()
    {
        Assert.Equal("secret", FieldValueConverter.ToStorage(FieldType.Password, "secret"));
        Assert.Throws<FormatException>(() => FieldValueConverter.ToStorage(FieldType.Password, new string('a', FieldValueConverter.MaxPasswordCharacters + 1)));
        Assert.Throws<FormatException>(() => FieldValueConverter.ToStorage(FieldType.Password, 12));
    }

    [Fact]
    public void FromStorage_converts_database_types_to_api_friendly_types()
    {
        Assert.Equal(new DateOnly(2026, 1, 2), FieldValueConverter.FromStorage(FieldType.Date, new DateTime(2026, 1, 2)));
        Assert.Equal(new TimeOnly(1, 2, 3), FieldValueConverter.FromStorage(FieldType.Time, new TimeSpan(1, 2, 3)));
        Assert.Null(FieldValueConverter.FromStorage(FieldType.Data, DBNull.Value));
        Assert.Equal("2500.75", ((decimal)FieldValueConverter.FromStorage(FieldType.Currency, 2500.750000000m)!).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Series_pattern_is_parsed_and_formatted()
    {
        var (prefix, digits) = NamingService.ParseSeries("CUST-.#####");

        Assert.Equal("CUST-", prefix);
        Assert.Equal(5, digits);
        Assert.Equal("CUST-00042", NamingService.FormatSeries(prefix, digits, 42));
        Assert.Equal("CUST-123456", NamingService.FormatSeries(prefix, digits, 123456));
    }
}
