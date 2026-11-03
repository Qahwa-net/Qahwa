using System.Globalization;
using System.Text;
using Qahwa.Abstractions;
using Qahwa.Core.Designer;

namespace Qahwa.Core;

/// <summary>Reads CSV text (RFC 4180): quoted cells, doubled quotes, line breaks inside quotes, CRLF or LF, an optional byte order mark.</summary>
public static class CsvReader
{
    public const int MaxCells = 500_000;

    /// <exception cref="FormatException">A quote is never closed, or there are too many cells.</exception>
    public static List<List<string>> Read(string text, int maxCells = MaxCells)
    {
        var rows = new List<List<string>>();
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var cells = 0;

        void EndCell()
        {
            row.Add(cell.ToString());
            cell.Clear();
            if (++cells > maxCells)
                throw new FormatException($"The file has more than {maxCells:N0} cells.");
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == ',') EndCell();
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                EndCell();
                if (row.Count > 1 || row[0].Length > 0)
                    rows.Add(row);
                row = [];
            }
            else cell.Append(c);
        }

        if (quoted)
            throw new FormatException("A quoted cell is never closed.");
        if (cell.Length > 0 || row.Count > 0)
        {
            EndCell();
            if (row.Count > 1 || row[0].Length > 0)
                rows.Add(row);
        }

        return rows;
    }
}

/// <param name="Table">For a column of a child table (written <c>table.field</c>, for example <c>lines.item</c>): the Table field it belongs to; <paramref name="Field"/> is then the child's field.</param>
/// <param name="Inner">For a column of a table inside a child table (<c>table.inner.field</c>, for example <c>lines.parts.qty</c>): the Table field of the child that holds it; <paramref name="Field"/> is then the inner row's field.</param>
public sealed record ImportColumn(string Header, string? Field, string? Label, string? Table = null, string? Inner = null);

public sealed record ImportRowError(int Row, string? Field, string Message);

public sealed record ImportPreview(
    IReadOnlyList<ImportColumn> Columns, int Rows, IReadOnlyList<IReadOnlyList<string>> Sample, IReadOnlyList<ImportRowError> Errors, bool MoreErrors);

/// <summary>How far a background import is: documents seen so far out of all, and what happened to them.</summary>
public sealed record ImportProgress(int Total, int Processed, int Created, int Updated, int Failed);

public sealed record ImportResult(int Created, int Updated, int Failed, IReadOnlyList<ImportRowError> Errors, bool Stopped, bool MoreErrors);

/// <summary>
/// Data import: documents from a CSV file, one row each. Every row goes through the ordinary insert or update as the person
/// importing, so permissions, validation, naming, workflow, assignment and notification rules all apply exactly as if they
/// had typed it. Rows that fail are reported with their line number and do not stop the others (unless asked to).
/// </summary>
public sealed class DataImportService(IMetaRegistry registry, DocumentService documents, PermissionService permissions)
{
    public const int MaxRows = 5000;
    public const int MaxBytes = 5 * 1024 * 1024;

    /// <summary>What a background import may take (it runs without anyone waiting, one document at a time, as the person).</summary>
    public const int LargeMaxRows = 50_000;
    public const int LargeMaxBytes = 25 * 1024 * 1024;
    public const int LargeMaxCells = 2_000_000;
    public const int MaxErrors = 100;
    public const int PreviewRows = 200;
    public const int SampleRows = 5;
    public const int ProgressEvery = 25;

    public static readonly string[] Modes = ["Insert", "Update"];

    private static readonly string[] True = ["1", "true", "yes", "y", "on"];
    private static readonly string[] False = ["0", "false", "no", "n", "off"];

    private static DocumentValidationException Invalid(string field, string message) => new([new FieldError(field, message)]);

    /// <summary>The fields a file may fill in: real columns the framework can set, not passwords, files, calculated or read-only ones.</summary>
    public static IReadOnlyList<DocFieldMeta> ImportableFields(DocTypeMeta meta) =>
        meta.Fields.Where(f => FieldTypes.HasColumn(f.Type) && f.Type is not (FieldType.Password or FieldType.Attach or FieldType.AttachImage)
            && f.Formula is null && !f.ReadOnly).ToList();

    private DocTypeMeta Require(string doctype, string mode, IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (!registry.TryGet(doctype, out var meta) || meta.IsChildTable || meta.IsSingle)
            throw new NotFoundException($"DocType '{doctype}' was not found.");
        if (!Modes.Contains(mode))
            throw Invalid("mode", "Use Insert or Update.");
        permissions.Require(meta, user, PermAction.Read);
        permissions.Require(meta, user, mode == "Insert" ? PermAction.Create : PermAction.Write);
        permissions.Require(meta, user, PermFlag.Import);
        return meta;
    }

    /// <summary>Checks that the person may import into this DocType in this mode (read, create or write, and the import right). Throws when not.</summary>
    public void Authorize(string doctype, string mode, IUserContext user) => Require(doctype, mode, user);

    /// <summary>Reads and checks a file for a background import and says how many rows it has (the caller stores the text).</summary>
    public int CheckLarge(string doctype, string csv, string mode, IUserContext user)
    {
        var meta = Require(doctype, mode, user);
        return Parse(meta, csv, mode, large: true).Rows.Count;
    }

    /// <summary>A file with just the header row, for a person to fill in.</summary>
    public string Template(string doctype, string mode, IUserContext user)
    {
        var meta = Require(doctype, "Insert", user);
        var names = ImportableFields(meta).Select(f => f.Name)
            .Concat(meta.Fields.Where(f => f.Type == FieldType.Table && f.Options is not null && registry.TryGet(f.Options, out _))
                .SelectMany(t => ImportableFields(registry.Get(t.Options!)).Select(c => $"{t.Name}.{c.Name}")));
        return string.Join(',', mode == "Update" ? names.Prepend("name") : names) + "\r\n";
    }

    private (List<ImportColumn> Columns, List<List<string>> Rows) Parse(DocTypeMeta meta, string csv, string mode, bool large = false)
    {
        var maxBytes = large ? LargeMaxBytes : MaxBytes;
        var maxRows = large ? LargeMaxRows : MaxRows;
        if (csv.Length > maxBytes)
            throw Invalid("csv", $"The file is larger than {maxBytes / (1024 * 1024)} MB.");
        List<List<string>> all;
        try
        {
            all = CsvReader.Read(csv, large ? LargeMaxCells : CsvReader.MaxCells);
        }
        catch (FormatException ex)
        {
            throw Invalid("csv", ex.Message);
        }

        if (all.Count == 0)
            throw Invalid("csv", "The file is empty. The first row must name the columns.");
        if (all.Count - 1 > maxRows)
            throw Invalid("csv", $"At most {maxRows:N0} rows per file{(large ? string.Empty : ", or up to " + LargeMaxRows.ToString("N0", CultureInfo.InvariantCulture) + " when imported in the background")}.");

        var fields = ImportableFields(meta);
        var columns = new List<ImportColumn>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var header in all[0].Select(h => h.Trim()))
        {
            ImportColumn column;
            var dot = header.IndexOf('.', StringComparison.Ordinal);
            var table = dot > 0 ? meta.Fields.FirstOrDefault(t => t.Type == FieldType.Table && t.Options is not null
                && (string.Equals(t.Name, header[..dot], StringComparison.OrdinalIgnoreCase) || (t.Label is not null && string.Equals(t.Label, header[..dot], StringComparison.OrdinalIgnoreCase)))) : null;
            if (table is not null && registry.TryGet(table.Options!, out var childMeta))
            {
                // "lines.item": a column of the child table, matched like any other by name or by label.
                // "lines.parts.qty": a column of the table inside the child table (one level deeper).
                var childFields = ImportableFields(childMeta);
                var part = header[(dot + 1)..].Trim();
                DocFieldMeta? child;
                string? innerName = null;
                var innerDot = part.IndexOf('.', StringComparison.Ordinal);
                var innerTable = innerDot > 0 ? childMeta.Fields.FirstOrDefault(t => t.Type == FieldType.Table && t.Options is not null
                    && (string.Equals(t.Name, part[..innerDot].Trim(), StringComparison.OrdinalIgnoreCase) || (t.Label is not null && string.Equals(t.Label, part[..innerDot].Trim(), StringComparison.OrdinalIgnoreCase)))) : null;
                if (innerTable is not null && registry.TryGet(innerTable.Options!, out var innerMeta))
                {
                    var innerPart = part[(innerDot + 1)..].Trim();
                    var innerFields = ImportableFields(innerMeta);
                    child = innerFields.FirstOrDefault(f => string.Equals(f.Name, innerPart, StringComparison.OrdinalIgnoreCase))
                        ?? innerFields.FirstOrDefault(f => f.Label is not null && string.Equals(f.Label, innerPart, StringComparison.OrdinalIgnoreCase));
                    innerName = innerTable.Name;
                    column = new ImportColumn(header, child?.Name, child is null ? null : $"{table.Label ?? table.Name} › {innerTable.Label ?? innerTable.Name} › {child.Label ?? child.Name}", child is null ? null : table.Name, child is null ? null : innerName);
                }
                else
                {
                    child = childFields.FirstOrDefault(f => string.Equals(f.Name, part, StringComparison.OrdinalIgnoreCase))
                        ?? childFields.FirstOrDefault(f => f.Label is not null && string.Equals(f.Label, part, StringComparison.OrdinalIgnoreCase));
                    column = new ImportColumn(header, child?.Name, child is null ? null : $"{table.Label ?? table.Name} › {child.Label ?? child.Name}", child is null ? null : table.Name);
                }
            }
            else
            {
                var field = fields.FirstOrDefault(f => string.Equals(f.Name, header, StringComparison.OrdinalIgnoreCase))
                    ?? fields.FirstOrDefault(f => f.Label is not null && string.Equals(f.Label, header, StringComparison.OrdinalIgnoreCase));
                string? target = field?.Name ?? (string.Equals(header, "name", StringComparison.OrdinalIgnoreCase) ? "name" : null);
                column = new ImportColumn(header, target, field?.Label ?? (target == "name" ? "ID" : null));
            }

            if (column.Field is not null && !used.Add((column.Table ?? string.Empty) + "." + (column.Inner ?? string.Empty) + "." + column.Field))
                throw Invalid("csv", $"The column '{(header.Length > 40 ? header[..40] : header)}' appears twice.");
            columns.Add(column);
        }

        if (mode == "Update" && columns.All(c => c.Field != "name"))
            throw Invalid("csv", "To update documents the file needs a column named name with each document's ID.");
        if (columns.All(c => c.Field is null))
            throw Invalid("csv", "None of the column names match a field of this DocType.");
        return (columns, all.Skip(1).ToList());
    }

    private static string Qualify(string? table, string field, string? inner = null) => table is null ? field : inner is null ? $"{table}.{field}" : $"{table}.{inner}.{field}";

    private Dictionary<string, object?> RowValues(DocTypeMeta meta, IReadOnlyList<ImportColumn> columns, IReadOnlyList<string> row, string mode, int line, List<ImportRowError> errors, string? table = null, string? inner = null)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var c = 0; c < columns.Count; c++)
        {
            if (columns[c].Field is not { } field || columns[c].Table != table || columns[c].Inner != inner)
                continue;
            var raw = c < row.Count ? row[c].Trim() : string.Empty;
            // Exported text that starts with = + - @ is saved with a leading apostrophe; take it off again.
            if (raw.Length > 1 && raw[0] == '\'' && raw[1] is '=' or '+' or '-' or '@')
                raw = raw[1..];

            var meta2 = meta.Fields.FirstOrDefault(f => f.Name == field);
            if (raw.Length == 0)
            {
                if (field != "name" && mode == "Insert")
                    continue; // blank means "use the default" when making a document
                values[field] = null;
                continue;
            }

            if (meta2?.Type == FieldType.Check)
            {
                var low = raw.ToLowerInvariant();
                if (True.Contains(low)) values[field] = true;
                else if (False.Contains(low)) values[field] = false;
                else errors.Add(new ImportRowError(line, Qualify(table, field, inner), "Use yes or no (or 1 or 0)."));
                continue;
            }

            if (meta2?.Type == FieldType.Select && !meta2.SelectOptions().Contains(raw, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(new ImportRowError(line, Qualify(table, field, inner), $"Choose one of: {string.Join(", ", meta2.SelectOptions())}."));
                continue;
            }

            if (meta2 is not null && FieldTypes.IsTextual(meta2.Type) is false)
            {
                try
                {
                    FieldValueConverter.ToStorage(meta2.Type, raw);
                }
                catch (FormatException)
                {
                    errors.Add(new ImportRowError(line, Qualify(table, field, inner), $"'{(raw.Length > 30 ? raw[..30] : raw)}' is not a valid {meta2.Type.ToString().ToLowerInvariant()}."));
                    continue;
                }
            }

            values[field] = raw;
        }

        return values;
    }

    /// <summary>One row of a child table, with its line in the file and the rows it has in a table of its own.</summary>
    private sealed class FileRow(int line, Dictionary<string, object?> values)
    {
        public int Line { get; } = line;
        public Dictionary<string, object?> Values { get; } = values;
        public Dictionary<string, List<FileRow>> Inner { get; } = new(StringComparer.Ordinal);

        /// <summary>The values to save: the row's own plus its inner tables.</summary>
        public Dictionary<string, object?> ToValues()
        {
            var all = new Dictionary<string, object?>(Values);
            foreach (var (name, rows) in Inner)
                all[name] = rows.Select(r => r.ToValues()).ToList();
            return all;
        }
    }

    /// <summary>One document of a file: its own values and the rows it has in each child table, each with its line in the file.</summary>
    private sealed class FileDocument(int line, Dictionary<string, object?> values, string? name)
    {
        public int Line { get; } = line;
        public Dictionary<string, object?> Values { get; } = values;
        public string? Name { get; } = name;
        public Dictionary<string, List<FileRow>> Tables { get; } = new(StringComparer.Ordinal);
        public List<ImportRowError> Problems { get; } = [];
    }

    /// <summary>
    /// Turns the rows of a file into documents. Without child columns every row is a document. With them, a row that leaves every column of
    /// the document itself blank (and for an update, the ID) belongs to the document above it and only adds a row to its child tables.
    /// </summary>
    private List<FileDocument> Documents(DocTypeMeta meta, IReadOnlyList<ImportColumn> columns, IReadOnlyList<List<string>> rows, string mode)
    {
        var nameColumn = columns.ToList().FindIndex(c => c.Field == "name" && c.Table is null);
        var hasChildColumns = columns.Any(c => c.Table is not null && c.Field is not null);
        var parentCells = columns.Select((c, i) => (c, i)).Where(x => x.c.Table is null && x.c.Field is not null).Select(x => x.i).ToList();
        var documents = new List<FileDocument>();
        for (var i = 0; i < rows.Count; i++)
        {
            var line = i + 2;
            var row = rows[i];
            bool Blank(int cell) => cell >= row.Count || row[cell].Trim().Length == 0;
            var continuation = hasChildColumns && documents.Count > 0 && parentCells.All(Blank);
            var problems = new List<ImportRowError>();
            FileDocument document;
            if (continuation)
                document = documents[^1];
            else
            {
                var name = nameColumn >= 0 && !Blank(nameColumn) ? row[nameColumn].Trim() : null;
                document = new FileDocument(line, RowValues(meta, columns, row, mode, line, problems), name);
                documents.Add(document);
            }

            foreach (var table in columns.Where(c => c.Table is not null && c.Field is not null).Select(c => c.Table!).Distinct(StringComparer.Ordinal))
            {
                var lineCells = columns.Select((c, idx) => (c, idx)).Where(x => x.c.Table == table && x.c.Inner is null && x.c.Field is not null).Select(x => x.idx).ToList();
                var filledInner = columns.Where(c => c.Table == table && c.Inner is not null && c.Field is not null).Select(c => c.Inner!).Distinct(StringComparer.Ordinal)
                    .Where(inner => columns.Select((c, idx) => (c, idx)).Any(x => x.c.Table == table && x.c.Inner == inner && x.c.Field is not null && !Blank(x.idx))).ToList();
                var lineBlank = lineCells.All(Blank);
                if (lineBlank && filledInner.Count == 0)
                    continue; // this row has nothing for this table
                var childMeta = registry.Get(meta.Fields.First(f => f.Name == table).Options!);
                if (!document.Tables.TryGetValue(table, out var list))
                    document.Tables[table] = list = [];
                FileRow target;
                if (lineBlank)
                {
                    // Only inner rows on this line of the file: they belong to the row above, as a row with no document columns belongs to the document above.
                    if (list.Count == 0)
                    {
                        problems.Add(new ImportRowError(line, Qualify(table, filledInner[0]), "These rows have no row above them to belong to."));
                        continue;
                    }

                    target = list[^1];
                }
                else
                {
                    target = new FileRow(line, RowValues(childMeta, columns, row, "Insert", line, problems, table));
                    list.Add(target);
                }

                foreach (var inner in filledInner)
                {
                    var innerMeta = registry.Get(childMeta.Fields.First(f => f.Name == inner).Options!);
                    if (!target.Inner.TryGetValue(inner, out var innerRows))
                        target.Inner[inner] = innerRows = [];
                    innerRows.Add(new FileRow(line, RowValues(innerMeta, columns, row, "Insert", line, problems, table, inner)));
                }
            }

            document.Problems.AddRange(problems);
        }

        return documents;
    }

    /// <summary>The line in the file a server error belongs to: for <c>lines[1].qty</c>, the line of the second row of <c>lines</c>.</summary>
    private static (int Line, string? Field) Locate(FileDocument document, string? field)
    {
        var pattern = new System.Text.RegularExpressions.Regex(@"^([A-Za-z0-9_]+)\[(\d+)\]\.?(.*)$");
        if (!string.IsNullOrEmpty(field) && pattern.Match(field) is { Success: true } m
            && document.Tables.TryGetValue(m.Groups[1].Value, out var rows) && int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) is var index && index < rows.Count)
        {
            var row = rows[index];
            var rest = m.Groups[3].Value;
            // "lines[0].parts[1].qty": the line of the second part of the first line.
            if (pattern.Match(rest) is { Success: true } n && row.Inner.TryGetValue(n.Groups[1].Value, out var innerRows)
                && int.Parse(n.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) is var innerIndex && innerIndex < innerRows.Count)
                return (innerRows[innerIndex].Line, n.Groups[3].Value.Length == 0 ? $"{m.Groups[1].Value}.{n.Groups[1].Value}" : $"{m.Groups[1].Value}.{n.Groups[1].Value}.{n.Groups[3].Value}");
            return (row.Line, rest.Length == 0 ? m.Groups[1].Value : $"{m.Groups[1].Value}.{rest}");
        }

        return (document.Line, string.IsNullOrEmpty(field) ? null : field);
    }

    /// <summary>Checks a file without importing anything: which columns match, a sample of rows, and the problems found in the first rows.</summary>
    public Task<ImportPreview> PreviewAsync(string doctype, string csv, string mode, IUserContext user, CancellationToken ct = default, bool large = false)
    {
        var meta = Require(doctype, mode, user);
        var (columns, rows) = Parse(meta, csv, mode, large);
        var errors = new List<ImportRowError>();
        var more = false;
        var lastLine = Math.Min(rows.Count, PreviewRows) + 1;
        var nameColumn = columns.FindIndex(c => c.Field == "name" && c.Table is null);
        foreach (var document in Documents(meta, columns, rows, mode).Where(d => d.Line <= lastLine))
        {
            errors.AddRange(document.Problems.Where(p => p.Row <= lastLine));
            if (mode == "Insert")
            {
                foreach (var required in ImportableFields(meta).Where(f => f.Required && f.Default is null))
                {
                    if (!document.Values.TryGetValue(required.Name, out var present) || present is null or "")
                        errors.Add(new ImportRowError(document.Line, required.Name, "This field is required."));
                }
            }

            if (mode == "Update" && nameColumn >= 0 && document.Name is null)
                errors.Add(new ImportRowError(document.Line, "name", "Every row needs the ID of the document to update."));
            if (mode == "Update" && nameColumn < 0)
                errors.Add(new ImportRowError(document.Line, "name", "Every row needs the ID of the document to update."));
            if (errors.Count > MaxErrors)
            {
                errors.RemoveRange(MaxErrors, errors.Count - MaxErrors);
                more = true;
                break;
            }
        }

        var width = columns.Count;
        var sample = rows.Take(SampleRows).Select(r => (IReadOnlyList<string>)Enumerable.Range(0, width).Select(i => i < r.Count ? Trim(r[i]) : string.Empty).ToList()).ToList();
        return Task.FromResult(new ImportPreview(columns, rows.Count, sample, errors.OrderBy(e => e.Row).ToList(), more));
    }

    private static string Trim(string cell) => cell.Length > 80 ? cell[..80] + "…" : cell;

    /// <summary>
/// Imports the file, one document at a time, as the person. A permission problem on the first document stops at once. With
/// <paramref name="progress"/> (background imports) it is told how far the import is every few documents and can stop it by returning false.
/// </summary>
    public async Task<ImportResult> RunAsync(
        string doctype, string csv, string mode, bool stopOnError, IUserContext user, CancellationToken ct = default,
        bool large = false, Func<ImportProgress, Task<bool>>? progress = null)
    {
        var meta = Require(doctype, mode, user);
        var (columns, rows) = Parse(meta, csv, mode, large);
        var errors = new List<ImportRowError>();
        int created = 0, updated = 0, failed = 0;
        var more = false;
        var stopped = false;

        void Fail(ImportRowError error)
        {
            failed++;
            if (errors.Count < MaxErrors) errors.Add(error);
            else more = true;
        }

        var all = Documents(meta, columns, rows, mode);
        var done = 0;
        foreach (var document in all)
        {
            if (ct.IsCancellationRequested)
                break;
            if (progress is not null && done > 0 && done % ProgressEvery == 0
                && !await progress(new ImportProgress(all.Count, done, created, updated, failed)))
            {
                stopped = true;
                break;
            }

            done++;
            if (document.Problems.Count > 0)
            {
                Fail(document.Problems[0]);
                if (stopOnError) { stopped = true; break; }
                continue;
            }

            var values = new Dictionary<string, object?>(document.Values);
            values.Remove("name");
            foreach (var (table, childRows) in document.Tables)
                values[table] = childRows.Select(r => r.ToValues()).ToList();

            try
            {
                if (mode == "Insert")
                {
                    if (document.Name is { } wanted && meta.Autoname == "prompt")
                        values["name"] = wanted;
                    await documents.InsertAsync(doctype, values, user, ct);
                    created++;
                }
                else
                {
                    var name = document.Name ?? throw Invalid("name", "Every row needs the ID of the document to update.");
                    await documents.UpdateAsync(doctype, name, values, user, ct);
                    updated++;
                }
            }
            catch (DocumentValidationException ex)
            {
                foreach (var e in ex.Errors.Take(3))
                {
                    var (line, field) = Locate(document, e.Field);
                    Fail(new ImportRowError(line, field, e.Message));
                }

                if (stopOnError) { stopped = true; break; }
            }
            catch (NotFoundException)
            {
                Fail(new ImportRowError(document.Line, "name", mode == "Update" ? "No document has this ID." : "Something this row refers to was not found."));
                if (stopOnError) { stopped = true; break; }
            }
            catch (ConflictException ex)
            {
                Fail(new ImportRowError(document.Line, null, ex.Message));
                if (stopOnError) { stopped = true; break; }
            }
            catch (PermissionDeniedException) when (created + updated == 0)
            {
                throw; // nothing was done and nothing will be: tell the person plainly
            }
            catch (PermissionDeniedException ex)
            {
                Fail(new ImportRowError(document.Line, null, ex.Message));
                if (stopOnError) { stopped = true; break; }
            }
        }

        return new ImportResult(created, updated, failed, errors, stopped, more);
    }
}
