using Qahwa.Abstractions;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class NewFieldTypeTests
{
    private static readonly string Png = "data:image/png;base64," + Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4]);

    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);

    public NewFieldTypeTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Visit", "autoname": "VIS-.####", "fields": [
                { "name": "who", "type": "Data" }, { "name": "stars", "type": "Rating" }, { "name": "spent", "type": "Duration" },
                { "name": "tint", "type": "Color" }, { "name": "code", "type": "Barcode" }, { "name": "place", "type": "Geolocation" }, { "name": "sign", "type": "Signature" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "report": true } ] }
            """));
    }

    private static object? Store(FieldType type, object? raw) => FieldValueConverter.ToStorage(type, raw);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData("5", 5)]
    public void A_rating_is_a_whole_number_of_stars_from_0_to_5(object raw, int expected) => Assert.Equal(expected, Store(FieldType.Rating, raw));

    [Theory]
    [InlineData(6)]
    [InlineData(-1)]
    [InlineData(2.5)]
    [InlineData("many")]
    [InlineData(true)]
    public void Other_ratings_are_refused(object raw) => Assert.Throws<FormatException>(() => Store(FieldType.Rating, raw));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5400, 5400)]
    [InlineData("90", 90)]
    [InlineData(315_360_000, 315_360_000)]
    public void A_duration_is_a_whole_number_of_seconds(object raw, int expected) => Assert.Equal(expected, Store(FieldType.Duration, raw));

    [Theory]
    [InlineData(-5)]
    [InlineData(1.5)]
    [InlineData(315_360_001)]
    [InlineData("1h")]
    public void Other_durations_are_refused(object raw) => Assert.Throws<FormatException>(() => Store(FieldType.Duration, raw));

    [Theory]
    [InlineData("#1A2B3C", "#1a2b3c")]
    [InlineData("  #ffffff ", "#ffffff")]
    public void A_colour_is_six_hex_digits_in_lower_case(string raw, string expected) => Assert.Equal(expected, Store(FieldType.Color, raw));

    [Theory]
    [InlineData("red")]
    [InlineData("#fff")]
    [InlineData("#gggggg")]
    [InlineData("#1a2b3c; background: url(x)")]
    [InlineData("<script>")]
    public void Other_colours_are_refused(string raw) => Assert.Throws<FormatException>(() => Store(FieldType.Color, raw));

    [Fact]
    public void An_empty_colour_geolocation_or_signature_is_no_value()
    {
        Assert.Null(Store(FieldType.Color, ""));
        Assert.Null(Store(FieldType.Geolocation, "  "));
        Assert.Null(Store(FieldType.Signature, ""));
        Assert.Null(Store(FieldType.Rating, ""));
        Assert.Null(Store(FieldType.Duration, ""));
    }

    [Theory]
    [InlineData("calendar", "calendar")]
    [InlineData(" userPlus ", "userPlus")]
    public void An_icon_is_a_name_of_letters_and_digits(string raw, string expected) => Assert.Equal(expected, Store(FieldType.Icon, raw));

    [Theory]
    [InlineData("Calendar")]
    [InlineData("1icon")]
    [InlineData("a-b")]
    [InlineData("<svg onload=x>")]
    [InlineData("../x")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Other_icon_names_are_refused(string raw) => Assert.Throws<FormatException>(() => Store(FieldType.Icon, raw));

    [Fact]
    public void An_empty_icon_is_no_value_and_the_designer_offers_icons()
    {
        Assert.Null(Store(FieldType.Icon, ""));
        Assert.True(Qahwa.Core.Designer.FieldTypeCatalog.IsSupported(FieldType.Icon));
    }

    [Fact]
    public void A_barcode_is_plain_text_without_control_characters()
    {
        Assert.Equal("6281234567890", Store(FieldType.Barcode, " 6281234567890 "));
        Assert.Throws<FormatException>(() => Store(FieldType.Barcode, "12\n34"));
        Assert.Throws<FormatException>(() => Store(FieldType.Barcode, "12\0"));
    }

    [Theory]
    [InlineData("""{"lat":24.7136,"lng":46.6753}""", """{"lat":24.7136,"lng":46.6753}""")]
    [InlineData("""{ "lng": 46.67531234567, "lat": 24.71 }""", """{"lat":24.71,"lng":46.675312}""")]
    [InlineData("""{"lat":-90,"lng":180}""", """{"lat":-90,"lng":180}""")]
    public void A_place_is_stored_in_one_normal_form(string raw, string expected) => Assert.Equal(expected, Store(FieldType.Geolocation, raw));

    [Theory]
    [InlineData("""{"lat":91,"lng":0}""")]
    [InlineData("""{"lat":0,"lng":181}""")]
    [InlineData("""{"lat":"24","lng":46}""")]
    [InlineData("""{"lat":24}""")]
    [InlineData("""{"lat":1,"lng":2,"extra":"<script>"}""")]
    [InlineData("""[24,46]""")]
    [InlineData("24.7,46.7")]
    [InlineData("not json")]
    public void Other_places_are_refused(string raw) => Assert.Throws<FormatException>(() => Store(FieldType.Geolocation, raw));

    [Fact]
    public void A_signature_must_really_be_a_small_png()
    {
        Assert.Equal(Png, Store(FieldType.Signature, Png));
        Assert.Throws<FormatException>(() => Store(FieldType.Signature, "data:image/svg+xml;base64," + Convert.ToBase64String("<svg onload=alert(1)>"u8.ToArray())));
        Assert.Throws<FormatException>(() => Store(FieldType.Signature, "data:image/png;base64," + Convert.ToBase64String("<html>not a png</html>"u8.ToArray()))); // claims PNG, is not
        Assert.Throws<FormatException>(() => Store(FieldType.Signature, "https://evil.example/x.png"));
        Assert.Throws<FormatException>(() => Store(FieldType.Signature, "data:image/png;base64,@@@not base64@@@"));
        Assert.Throws<FormatException>(() => Store(FieldType.Signature, "data:image/png;base64," + new string('A', FieldValueConverter.MaxSignatureCharacters)));
    }

    [Fact]
    public async Task The_values_are_saved_read_back_and_checked_when_a_document_is_saved()
    {
        var made = await _engine.Documents.InsertAsync("Visit", Values(("who", "Sam"), ("stars", 4), ("spent", 5400), ("tint", "#AABBCC"), ("code", "123456"), ("place", """{"lat":24.7,"lng":46.7}"""), ("sign", Png)), Ann);

        var saved = await _engine.Documents.GetAsync("Visit", (string)made["name"]!, Ann);

        Assert.Equal(4, Convert.ToInt32(saved["stars"]));
        Assert.Equal(5400, Convert.ToInt32(saved["spent"]));
        Assert.Equal("#aabbcc", saved["tint"]);
        Assert.Equal("""{"lat":24.7,"lng":46.7}""", saved["place"]);
        Assert.Equal(Png, saved["sign"]);
        var error = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Visit", Values(("stars", 9), ("tint", "blue")), Ann));
        Assert.Contains(error.Errors, e => e.Field == "stars");
        Assert.Contains(error.Errors, e => e.Field == "tint");
    }

    [Fact]
    public async Task Ratings_and_durations_can_be_averaged_and_grouped_in_reports()
    {
        foreach (var stars in new[] { 5, 3, 4 })
            await _engine.Documents.InsertAsync("Visit", Values(("who", "S"), ("stars", stars), ("spent", 60 * stars)), Ann);

        var run = await _engine.Reports.RunAsync(new ReportDefinition { DocType = "Visit", GroupBy = "who", Aggregates = [new ReportAggregate("avg", "stars"), new ReportAggregate("sum", "spent")] }, Ann);

        Assert.Equal(4m, run.Rows[0]["avg_stars"]);
        Assert.Equal(720m, run.Rows[0]["sum_spent"]);
    }

    [Theory]
    [InlineData(0L, "0s")]
    [InlineData(45L, "45s")]
    [InlineData(5400L, "1h 30m")]
    [InlineData(93600L, "1d 2h")]
    [InlineData(86461L, "1d 1m")]
    public void Durations_are_printed_as_the_two_largest_units(long seconds, string expected) => Assert.Equal(expected, PrintTemplate.Duration(seconds));

    [Fact]
    public void Printouts_show_stars_and_places_but_never_the_signature_picture()
    {
        var meta = _engine.Registry.Get("Visit");
        string Show(string field, object? value) => PrintTemplate.Show(meta.Fields.First(f => f.Name == field), value);

        Assert.Equal("★★★☆☆", Show("stars", 3));
        Assert.Equal("1h 30m", Show("spent", 5400));
        Assert.Equal("24.7, 46.7", Show("place", """{"lat":24.7,"lng":46.7}"""));
        Assert.Equal("", Show("place", "not json"));
        Assert.Equal("(signed)", Show("sign", Png));
        Assert.DoesNotContain("data:", Show("sign", Png));
    }

    [Fact]
    public void The_designer_offers_the_new_types()
    {
        foreach (var type in new[] { FieldType.Rating, FieldType.Duration, FieldType.Color, FieldType.Barcode, FieldType.Geolocation, FieldType.Signature })
            Assert.True(Qahwa.Core.Designer.FieldTypeCatalog.IsSupported(type), type.ToString());
    }
}
