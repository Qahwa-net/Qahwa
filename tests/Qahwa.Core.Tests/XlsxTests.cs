using System.IO.Compression;
using System.Text;

namespace Qahwa.Core.Tests;

public class XlsxTests
{
    private static Dictionary<string, object?> Row(params (string, object?)[] cells) => cells.ToDictionary(c => c.Item1, c => c.Item2);

    private static byte[] Zip(Dictionary<string, string> parts, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in parts)
                using (var w = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false)))
                    w.Write(content);
        return output.ToArray();
    }

    private const string Ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"";
    private const string Rel = "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    private static Dictionary<string, string> Package(string sheet, string? shared = null, string? styles = null, string target = "worksheets/sheet1.xml", string sheetPath = "xl/worksheets/sheet1.xml")
    {
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>",
            ["xl/workbook.xml"] = $"<workbook {Ns} {Rel}><sheets><sheet name=\"A\" sheetId=\"1\" r:id=\"rId7\"/><sheet name=\"B\" sheetId=\"2\" r:id=\"rId8\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId7\" Type=\"x\" Target=\"{target}\"/><Relationship Id=\"rId8\" Type=\"x\" Target=\"worksheets/sheet2.xml\"/></Relationships>",
            [sheetPath] = sheet,
            ["xl/worksheets/sheet2.xml"] = $"<worksheet {Ns}><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>second sheet</t></is></c></row></sheetData></worksheet>",
        };
        if (shared is not null) parts["xl/sharedStrings.xml"] = shared;
        if (styles is not null) parts["xl/styles.xml"] = styles;
        return parts;
    }

    // ---- writing, and reading what was written ----

    [Fact]
    public void What_is_written_reads_back_exactly_including_other_scripts_and_awkward_text()
    {
        var columns = new[] { "name", "qty", "price", "active", "due", "seen", "note" };
        var rows = new[]
        {
            Row(("name", "Acme & <Sons> \"Ltd\""), ("qty", 3), ("price", 1234.5m), ("active", true), ("due", new DateOnly(2026, 3, 4)), ("seen", new DateTime(2026, 3, 4, 10, 30, 15, DateTimeKind.Utc)), ("note", "line1\nline2\ttab")),
            Row(("name", "مقهى الصباح ☕ 日本語"), ("qty", 0), ("price", -0.000001m), ("active", false), ("due", null), ("seen", null), ("note", "=1+1")),
            Row(("name", "  spaces  "), ("qty", long.MaxValue), ("price", 1e15), ("active", null), ("due", null), ("seen", null), ("note", new string('x', 40_000))),
        };

        var read = XlsxReader.Read(XlsxWriter.Write(columns, rows));

        Assert.Equal(columns, read[0]);
        Assert.Equal(["Acme & <Sons> \"Ltd\"", "3", "1234.5", "1", "2026-03-04", "2026-03-04 10:30:15", "line1\nline2\ttab"], read[1]);
        Assert.Equal("مقهى الصباح ☕ 日本語", read[2][0]);
        Assert.Equal("=1+1", read[2][6]); // text stays text
        Assert.Equal("  spaces  ", read[3][0]);
        Assert.Equal(long.MaxValue.ToString(), read[3][1]);
        Assert.Equal(XlsxWriter.MaxCellText, read[3][6].Length); // cut to what a cell can hold
        Assert.Equal(4, read.Count);
    }

    [Fact]
    public void Control_characters_xml_cannot_carry_are_dropped_not_fatal()
    {
        var read = XlsxReader.Read(XlsxWriter.Write(["a"], [Row(("a", "x\u0001y\u0000z￾end"))]));

        Assert.Equal("xyzend", read[1][0]);
    }

    [Fact]
    public void The_file_is_a_real_zip_with_the_usual_parts_and_a_bold_header()
    {
        var bytes = XlsxWriter.Write(["h"], [Row(("h", "v"))], "Customers: [all]/?");

        using var zip = new ZipArchive(new MemoryStream(bytes));
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("xl/workbook.xml", names);
        Assert.Contains("xl/worksheets/sheet1.xml", names);
        using var workbook = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open());
        Assert.Contains("name=\"Customers all\"", workbook.ReadToEnd()); // characters a sheet name cannot have are dropped
        using var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        Assert.Contains("s=\"1\"", sheet.ReadToEnd());
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void Columns_are_lettered_like_a_spreadsheet(int index, string letters) => Assert.Equal(letters, XlsxWriter.Column(index));

    // ---- reading files made by other programs ----

    [Fact]
    public void A_file_made_by_a_spreadsheet_program_reads_with_shared_strings_dates_and_gaps()
    {
        var sheet = $"<x:worksheet xmlns:x=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><x:sheetData>" +
                    "<x:row r=\"1\"><x:c r=\"A1\" t=\"s\"><x:v>0</x:v></x:c><x:c r=\"C1\" t=\"s\"><x:v>1</x:v></x:c></x:row>" +
                    "<x:row r=\"2\"><x:c r=\"A2\"><x:v>12.5</x:v></x:c><x:c r=\"B2\" s=\"1\"><x:v>46085</x:v></x:c><x:c r=\"C2\" s=\"2\"><x:v>46085.5</x:v></x:c><x:c r=\"D2\" t=\"b\"><x:v>1</x:v></x:c></x:row>" +
                    "<x:row r=\"3\"/><x:row r=\"4\"><x:c r=\"B4\" t=\"str\"><x:f>A2*2</x:f><x:v>25</x:v></x:c><x:c r=\"C4\"/></x:row>" +
                    "</x:sheetData></x:worksheet>";
        var shared = $"<sst {Ns}><si><t>Name</t></si><si><r><t>Rich </t></r><r><t>text</t></r></si></sst>";
        var styles = $"<styleSheet {Ns}><numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"dd/mm/yyyy hh:mm\"/></numFmts><cellXfs count=\"3\"><xf numFmtId=\"0\"/><xf numFmtId=\"14\"/><xf numFmtId=\"164\"/></cellXfs></styleSheet>";

        var read = XlsxReader.Read(Zip(Package(sheet, shared, styles)));

        Assert.Equal([["Name", "", "Rich text"], ["12.5", "2026-03-04", "2026-03-04 12:00:00", "1"], ["", "25"]], read);
    }

    [Fact]
    public void Only_the_first_sheet_is_read_and_the_workbook_may_name_its_parts_oddly()
    {
        var sheet = $"<worksheet {Ns}><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>first</t></is></c></row></sheetData></worksheet>";

        var read = XlsxReader.Read(Zip(Package(sheet, target: "/xl/data/one.xml", sheetPath: "xl/data/one.xml")));

        Assert.Equal([["first"]], read);
    }

    [Theory]
    [InlineData("../../evil.xml")]
    [InlineData("../../../../etc/passwd")]
    [InlineData("missing.xml")]
    [InlineData("")]
    public void A_sheet_that_points_outside_the_package_or_nowhere_is_refused(string target)
    {
        var sheet = $"<worksheet {Ns}><sheetData/></worksheet>";

        Assert.Throws<FormatException>(() => XlsxReader.Read(Zip(Package(sheet, target: target))));
    }

    [Fact]
    public void Formulas_are_never_run_only_the_stored_value_is_read()
    {
        var sheet = $"<worksheet {Ns}><sheetData><row r=\"1\"><c r=\"A1\"><f>cmd|' /C calc'!A0</f><v>stored</v></c><c r=\"B1\"><f>HYPERLINK(\"http://evil\",\"x\")</f></c></row></sheetData></worksheet>";

        var read = XlsxReader.Read(Zip(Package(sheet)));

        Assert.Equal([["stored"]], read); // the second cell has no stored value: empty
    }

    // ---- hostile files ----

    [Fact]
    public void A_zip_bomb_is_stopped_by_what_is_really_read()
    {
        var bomb = new string('0', 60 * 1024 * 1024); // unpacks far beyond the limit, packs to almost nothing
        var sheet = $"<worksheet {Ns}><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>{bomb}</t></is></c></row></sheetData></worksheet>";
        var file = Zip(Package(sheet));

        Assert.True(file.Length < XlsxReader.MaxFileBytes);
        Assert.Contains("too large", Assert.Throws<FormatException>(() => XlsxReader.Read(file)).Message);
    }

    [Fact]
    public void Entity_tricks_and_dtds_are_refused()
    {
        var sheet = "<!DOCTYPE s [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;\">]>" + $"<worksheet {Ns}><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>&b;</t></is></c></row></sheetData></worksheet>";
        var external = "<!DOCTYPE s [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>" + $"<worksheet {Ns}><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>&x;</t></is></c></row></sheetData></worksheet>";

        Assert.Throws<FormatException>(() => XlsxReader.Read(Zip(Package(sheet))));
        Assert.Throws<FormatException>(() => XlsxReader.Read(Zip(Package(external))));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5 })]
    public void Not_a_spreadsheet_is_a_plain_error(byte[] bytes) => Assert.Throws<FormatException>(() => XlsxReader.Read(bytes));

    [Fact]
    public void A_zip_without_the_workbook_or_with_too_many_parts_or_too_big_is_refused()
    {
        Assert.Throws<FormatException>(() => XlsxReader.Read(Zip(new Dictionary<string, string> { ["readme.txt"] = "hi" })));
        var many = Enumerable.Range(0, XlsxReader.MaxParts + 1).ToDictionary(i => $"f{i}.txt", _ => "x");
        Assert.Throws<FormatException>(() => XlsxReader.Read(Zip(many)));
        Assert.Throws<FormatException>(() => XlsxReader.Read(new byte[XlsxReader.MaxFileBytes + 1]));
    }

    [Fact]
    public void Absurd_cell_addresses_and_too_many_columns_rows_or_cells_are_refused()
    {
        var wide = $"<worksheet {Ns}><sheetData><row r=\"1\"><c r=\"ZZZZ1\"><v>1</v></c></row></sheetData></worksheet>";
        var huge = $"<worksheet {Ns}><sheetData><row r=\"1\"><c r=\"{new string('Z', 50)}1\"><v>1</v></c></row></sheetData></worksheet>";
        var rows = $"<worksheet {Ns}><sheetData>" + string.Concat(Enumerable.Range(1, XlsxReader.MaxRows + 1).Select(i => $"<row r=\"{i}\"/>")) + "</sheetData></worksheet>";

        Assert.Throws<FormatException>(() => XlsxReader.Read(Zip(Package(wide))));
        Assert.Throws<FormatException>(() => XlsxReader.Read(Zip(Package(huge))));
        Assert.Throws<FormatException>(() => XlsxReader.Read(Zip(Package(rows))));
    }

    [Fact]
    public void A_shared_string_index_out_of_range_is_just_empty_and_dates_outside_the_calendar_stay_numbers()
    {
        var sheet = $"<worksheet {Ns}><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>999</v></c><c r=\"B1\" t=\"s\"><v>-1</v></c><c r=\"C1\" s=\"1\"><v>99999999999</v></c><c r=\"D1\" t=\"s\"><v>abc</v></c><c r=\"E1\">ok</c></row></sheetData></worksheet>";
        var styles = $"<styleSheet {Ns}><cellXfs count=\"2\"><xf numFmtId=\"0\"/><xf numFmtId=\"14\"/></cellXfs></styleSheet>";

        var read = XlsxReader.Read(Zip(Package(sheet, shared: $"<sst {Ns}><si><t>x</t></si></sst>", styles: styles)));

        Assert.Equal([["", "", "99999999999"]], read.Select(r => r.Take(3).ToList()).ToList()); // the rest of the row is empty text
    }

    // ---- csv for the import ----

    [Fact]
    public void Rows_become_csv_the_existing_import_can_read()
    {
        var rows = new List<List<string>> { new() { "name", "note" }, new() { "Acme, Inc.", "say \"hi\"\nbye" }, new() { "plain", "" } };

        var parsed = CsvReader.Read(XlsxReader.ToCsv(rows));

        Assert.Equal(rows.Select(r => r.ToArray()).ToArray(), parsed.Select(r => r.ToArray()).ToArray());
    }
}
