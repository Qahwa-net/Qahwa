using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Qahwa.Core;

/// <summary>Writes a spreadsheet (.xlsx, Office Open XML) with one sheet: a bold header row, then the rows, with real numbers, dates and moments.</summary>
public static class XlsxWriter
{
    public const int MaxRows = 1_000_000;
    public const int MaxCellText = 32_767;

    public static byte[] Write(IReadOnlyList<string> columns, IEnumerable<IReadOnlyDictionary<string, object?>> rows, string sheetName = "Sheet1")
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
            Add(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                $"xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"{Escape(SafeSheetName(sheetName))}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            Add(zip, "xl/styles.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"4\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
                "<xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/><xf numFmtId=\"22\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs>" +
                "</styleSheet>");

            var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            WriteRow(writer, 1, columns.Select(c => (object?)c), header: true);
            var rowNumber = 1;
            foreach (var row in rows)
            {
                if (++rowNumber > MaxRows)
                    break;
                WriteRow(writer, rowNumber, columns.Select(c => row.GetValueOrDefault(c)), header: false);
            }

            writer.Write("</sheetData></worksheet>");
        }

        return output.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string SafeSheetName(string name)
    {
        var clean = new string(name.Where(c => !"[]:*?/\\".Contains(c) && !char.IsControl(c)).ToArray()).Trim();
        return clean.Length is 0 ? "Sheet1" : clean.Length > 31 ? clean[..31] : clean;
    }

    public static string Column(int index)
    {
        var sb = new StringBuilder();
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
            sb.Insert(0, (char)('A' + (n - 1) % 26));
        return sb.ToString();
    }

    private static void WriteRow(StreamWriter w, int number, IEnumerable<object?> cells, bool header)
    {
        w.Write($"<row r=\"{number}\">");
        var column = 0;
        foreach (var value in cells)
        {
            var at = $"{Column(column++)}{number}";
            switch (value)
            {
                case null:
                    break;
                case bool b:
                    w.Write($"<c r=\"{at}\" t=\"b\"><v>{(b ? 1 : 0)}</v></c>");
                    break;
                case DateTime dt:
                    w.Write($"<c r=\"{at}\" s=\"3\"><v>{Serial(dt).ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                    break;
                case DateOnly d:
                    w.Write($"<c r=\"{at}\" s=\"2\"><v>{Serial(d.ToDateTime(TimeOnly.MinValue)).ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                    break;
                case TimeOnly t:
                    w.Write($"<c r=\"{at}\" t=\"inlineStr\"><is><t>{t.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}</t></is></c>");
                    break;
                case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    w.Write($"<c r=\"{at}\"><v>{Convert.ToString(value, CultureInfo.InvariantCulture)}</v></c>");
                    break;
                default:
                    var text = Clean(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                    // Text is always written as text (an inline string), so a cell such as =1+1 is shown, never run.
                    w.Write($"<c r=\"{at}\" t=\"inlineStr\"{(header ? " s=\"1\"" : string.Empty)}><is><t xml:space=\"preserve\">{Escape(text)}</t></is></c>");
                    break;
            }
        }

        w.Write("</row>");
    }

    private static double Serial(DateTime moment) =>
        ((moment.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(moment, DateTimeKind.Utc) : moment.ToUniversalTime()) - new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Utc)).TotalDays;

    /// <summary>Drops characters XML cannot carry and cuts text to what a cell may hold.</summary>
    private static string Clean(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, MaxCellText));
        foreach (var c in text)
        {
            if (sb.Length >= MaxCellText)
                break;
            if (c is '\t' or '\n' or '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD) || char.IsSurrogate(c))
                sb.Append(c);
        }

        return sb.ToString();
    }

    private static string Escape(string text) => System.Security.SecurityElement.Escape(text) ?? string.Empty;
}

/// <summary>
/// Reads the first sheet of a spreadsheet (.xlsx) as rows of text. The file is untrusted: it is opened as a zip with limits on the number
/// of parts and on the real (not the declared) size of what is read, paths inside it are never followed outside the package, XML is read with
/// DTDs off, formulas are never evaluated (the stored value is read), and the number of rows, columns and cells is limited.
/// </summary>
public static class XlsxReader
{
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxPartBytes = 40 * 1024 * 1024;
    public const int MaxParts = 100;
    public const int MaxRows = 50_000;
    public const int MaxColumns = 256;
    public const int MaxSharedStrings = 500_000;

    /// <exception cref="FormatException">The file is not a spreadsheet this reader can use, or it is larger than the limits.</exception>
    public static List<List<string>> Read(byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxFileBytes)
            throw new FormatException($"The spreadsheet must be at most {MaxFileBytes / 1024 / 1024} MB.");
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxParts)
                throw new FormatException("The spreadsheet has too many parts.");
            var parts = zip.Entries.ToDictionary(e => e.FullName, StringComparer.OrdinalIgnoreCase);
            if (parts.Count != zip.Entries.Count)
                throw new FormatException("The spreadsheet has repeated parts.");

            var sheetPath = FindFirstSheet(parts);
            var shared = parts.TryGetValue("xl/sharedStrings.xml", out var sharedEntry) ? ReadSharedStrings(sharedEntry) : [];
            var dateStyles = parts.TryGetValue("xl/styles.xml", out var stylesEntry) ? ReadDateStyles(stylesEntry) : [];
            return ReadSheet(parts[sheetPath], shared, dateStyles);
        }
        catch (InvalidDataException)
        {
            throw new FormatException("This is not a spreadsheet (.xlsx) file.");
        }
        catch (XmlException)
        {
            throw new FormatException("The spreadsheet could not be read.");
        }
    }

    private static XmlReader Open(ZipArchiveEntry entry)
    {
        // The size a part declares is not believed: what is actually read is counted.
        var bytes = ReadLimited(entry);
        return XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false, MaxCharactersInDocument = MaxPartBytes });
    }

    private static byte[] ReadLimited(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxPartBytes)
                throw new FormatException("The spreadsheet is too large when unpacked.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string FindFirstSheet(Dictionary<string, ZipArchiveEntry> parts)
    {
        if (!parts.TryGetValue("xl/workbook.xml", out var workbook) || !parts.TryGetValue("xl/_rels/workbook.xml.rels", out var rels))
            throw new FormatException("This is not a spreadsheet (.xlsx) file.");

        string? relId = null;
        using (var reader = Open(workbook))
        {
            while (relId is null && reader.Read())
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet")
                    relId = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships") ?? reader.GetAttribute("r:id");
        }

        if (relId is null)
            throw new FormatException("The spreadsheet has no sheet.");

        string? target = null;
        using (var reader = Open(rels))
        {
            while (target is null && reader.Read())
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship" && reader.GetAttribute("Id") == relId)
                    target = reader.GetAttribute("Target");
        }

        if (string.IsNullOrEmpty(target))
            throw new FormatException("The spreadsheet's first sheet could not be found.");
        // The target is relative to xl/ (or absolute inside the package). It must name a part of the package, nothing else.
        var path = (target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target).Replace('\\', '/');
        var segments = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part == "..")
            {
                if (segments.Count == 0)
                    throw new FormatException("The spreadsheet refers outside itself.");
                segments.RemoveAt(segments.Count - 1);
            }
            else if (part is not ("." or ""))
            {
                segments.Add(part);
            }
        }

        var normalised = string.Join('/', segments);
        return parts.ContainsKey(normalised) ? normalised : throw new FormatException("The spreadsheet's first sheet could not be found.");
    }

    private static List<string> ReadSharedStrings(ZipArchiveEntry entry)
    {
        var list = new List<string>();
        using var reader = Open(entry);
        var text = new StringBuilder();
        var inItem = false;
        var depthInPhonetic = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si" && !reader.IsEmptyElement)
            {
                inItem = true;
                text.Clear();
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                list.Add(string.Empty);
            }
            else if (inItem && reader.NodeType == XmlNodeType.Element && reader.LocalName == "rPh")
            {
                depthInPhonetic++; // the phonetic hint of an East Asian text is not part of the text
            }
            else if (inItem && reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "rPh")
            {
                depthInPhonetic--;
            }
            else if (inItem && depthInPhonetic == 0 && reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
            {
                text.Append(reader.ReadElementContentAsString());
                reader.MoveToContent();
            }

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "si")
            {
                inItem = false;
                list.Add(text.ToString());
                if (list.Count > MaxSharedStrings)
                    throw new FormatException("The spreadsheet has too many different texts.");
            }
        }

        return list;
    }

    /// <summary>Which cell styles show a date or a moment (so a stored number can be read as one).</summary>
    private static HashSet<int> ReadDateStyles(ZipArchiveEntry entry)
    {
        var customDate = new HashSet<int>();
        var styles = new HashSet<int>();
        using var reader = Open(entry);
        var inCellXfs = false;
        var index = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "numFmt" && int.TryParse(reader.GetAttribute("numFmtId"), out var id) && IsDateFormat(reader.GetAttribute("formatCode") ?? string.Empty))
                customDate.Add(id);
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "cellXfs")
                inCellXfs = true;
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "cellXfs")
                inCellXfs = false;
            else if (inCellXfs && reader.NodeType == XmlNodeType.Element && reader.LocalName == "xf")
            {
                if (int.TryParse(reader.GetAttribute("numFmtId"), out var fmt) && (fmt is (>= 14 and <= 22) or (>= 45 and <= 47) || customDate.Contains(fmt)))
                    styles.Add(index);
                index++;
            }
        }

        return styles;
    }

    private static bool IsDateFormat(string code)
    {
        var plain = System.Text.RegularExpressions.Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.|_.", string.Empty);
        return plain.IndexOfAny(['d', 'D', 'm', 'M', 'y', 'Y', 'h', 'H']) >= 0;
    }

    private static int ColumnIndex(string reference)
    {
        var n = 0;
        foreach (var c in reference)
        {
            if (!char.IsAsciiLetter(c))
                break;
            n = n * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
            if (n > 100_000)
                throw new FormatException("A cell address is not valid.");
        }

        return n - 1;
    }

    private static List<List<string>> ReadSheet(ZipArchiveEntry entry, List<string> shared, HashSet<int> dateStyles)
    {
        var rows = new List<List<string>>();
        var cells = 0;
        using var reader = Open(entry);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
                continue;
            if (rows.Count >= MaxRows)
                throw new FormatException($"The sheet has more than {MaxRows:N0} rows.");

            var row = new List<string>();
            rows.Add(row);
            if (reader.IsEmptyElement)
                continue;
            using var rowReader = reader.ReadSubtree();
            rowReader.Read();
            while (rowReader.Read())
            {
                if (rowReader.NodeType != XmlNodeType.Element || rowReader.LocalName != "c")
                    continue;
                var column = rowReader.GetAttribute("r") is { } at ? ColumnIndex(at) : row.Count;
                if (column is < 0 or >= MaxColumns)
                    throw new FormatException($"The sheet has more than {MaxColumns} columns.");
                var value = ReadCell(rowReader, shared, dateStyles);
                while (row.Count < column)
                    row.Add(string.Empty);
                if (column < row.Count)
                    row[column] = value;
                else
                    row.Add(value);
                if (++cells > CsvReader.MaxCells)
                    throw new FormatException($"The file has more than {CsvReader.MaxCells:N0} cells.");
            }
        }

        return Trim(rows);
    }

    /// <summary>The text of one cell (the reader is on its start tag and is left at its end).</summary>
    private static string ReadCell(XmlReader reader, List<string> shared, HashSet<int> dateStyles)
    {
        var type = reader.GetAttribute("t");
        var style = int.TryParse(reader.GetAttribute("s"), NumberStyles.None, CultureInfo.InvariantCulture, out var st) ? st : 0;
        if (reader.IsEmptyElement)
            return string.Empty;

        var raw = new StringBuilder();
        var inline = new StringBuilder();
        using (var cell = reader.ReadSubtree())
        {
            cell.Read();
            while (cell.Read())
            {
                if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "v")
                    raw.Append(cell.ReadElementContentAsString());
                else if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "t")
                    inline.Append(cell.ReadElementContentAsString());
            }
        }

        var value = type switch
        {
            "s" => int.TryParse(raw.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < shared.Count ? shared[i] : string.Empty,
            "inlineStr" => inline.ToString(),
            "b" => raw.ToString() == "1" ? "1" : "0",
            "str" or "e" => raw.ToString(),
            _ => dateStyles.Contains(style) && double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) ? FromSerial(serial) : raw.ToString(),
        };
        return value.Length > XlsxWriter.MaxCellText ? value[..XlsxWriter.MaxCellText] : value;
    }

    private static string FromSerial(double serial)
    {
        if (serial is < 1 or > 2_958_465)
            return serial.ToString("R", CultureInfo.InvariantCulture);
        var moment = new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Utc).AddDays(Math.Floor(serial)).AddSeconds(Math.Round((serial - Math.Floor(serial)) * 86400));
        return moment.TimeOfDay == TimeSpan.Zero ? moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : moment.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>Drops rows that are empty and empty cells at the end of each row.</summary>
    private static List<List<string>> Trim(List<List<string>> rows)
    {
        var result = new List<List<string>>();
        foreach (var row in rows)
        {
            var end = row.Count;
            while (end > 0 && row[end - 1].Length == 0)
                end--;
            if (end > 0)
                result.Add(row.GetRange(0, end));
        }

        return result;
    }

    /// <summary>The rows as CSV text, so the same import that reads CSV can read a spreadsheet.</summary>
    public static string ToCsv(IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
            sb.AppendJoin(',', row.Select(c => c.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c)).Append("\r\n");
        return sb.ToString();
    }
}
