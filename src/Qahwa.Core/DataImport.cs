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
    public static List<List<string>> Read(string text)
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
            if (++cells > MaxCells)
                throw new FormatException($"The file has more than {MaxCells:N0} cells.");
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

public sealed record ImportColumn(string Header, string? Field, string? Label);

public sealed record ImportRowError(int Row, string? Field, string Message);

public sealed record ImportPreview(
    IReadOnlyList<ImportColumn> Columns, int Rows, IReadOnlyList<IReadOnlyList<string>> Sample, IReadOnlyList<ImportRowError> Errors, bool MoreErrors);

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
    public const int MaxErrors = 100;
    public const int PreviewRows = 200;
    public const int SampleRows = 5;

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
        return meta;
    }

    /// <summary>A file with just the header row, for a person to fill in.</summary>
    public string Template(string doctype, string mode, IUserContext user)
    {
        var meta = Require(doctype, "Insert", user);
        var names = ImportableFields(meta).Select(f => f.Name);
        return string.Join(',', mode == "Update" ? names.Prepend("name") : names) + "\r\n";
    }

    private (List<ImportColumn> Columns, List<List<string>> Rows) Parse(DocTypeMeta meta, string csv, string mode)
    {
        if (csv.Length > MaxBytes)
            throw Invalid("csv", $"The file is larger than {MaxBytes / (1024 * 1024)} MB.");
        List<List<string>> all;
        try
        {
            all = CsvReader.Read(csv);
        }
        catch (FormatException ex)
        {
            throw Invalid("csv", ex.Message);
        }

        if (all.Count == 0)
            throw Invalid("csv", "The file is empty. The first row must name the columns.");
        if (all.Count - 1 > MaxRows)
            throw Invalid("csv", $"At most {MaxRows:N0} rows per file.");

        var fields = ImportableFields(meta);
        var columns = new List<ImportColumn>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var header in all[0].Select(h => h.Trim()))
        {
            var field = fields.FirstOrDefault(f => string.Equals(f.Name, header, StringComparison.OrdinalIgnoreCase))
                ?? fields.FirstOrDefault(f => f.Label is not null && string.Equals(f.Label, header, StringComparison.OrdinalIgnoreCase));
            string? target = field?.Name ?? (string.Equals(header, "name", StringComparison.OrdinalIgnoreCase) ? "name" : null);
            if (target is not null && !used.Add(target))
                throw Invalid("csv", $"The column '{(header.Length > 40 ? header[..40] : header)}' appears twice.");
            columns.Add(new ImportColumn(header, target, field?.Label ?? (target == "name" ? "ID" : null)));
        }

        if (mode == "Update" && columns.All(c => c.Field != "name"))
            throw Invalid("csv", "To update documents the file needs a column named name with each document's ID.");
        if (columns.All(c => c.Field is null))
            throw Invalid("csv", "None of the column names match a field of this DocType.");
        return (columns, all.Skip(1).ToList());
    }

    private Dictionary<string, object?> RowValues(DocTypeMeta meta, IReadOnlyList<ImportColumn> columns, IReadOnlyList<string> row, string mode, int line, List<ImportRowError> errors)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var c = 0; c < columns.Count; c++)
        {
            if (columns[c].Field is not { } field)
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
                else errors.Add(new ImportRowError(line, field, "Use yes or no (or 1 or 0)."));
                continue;
            }

            if (meta2?.Type == FieldType.Select && !meta2.SelectOptions().Contains(raw, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(new ImportRowError(line, field, $"Choose one of: {string.Join(", ", meta2.SelectOptions())}."));
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
                    errors.Add(new ImportRowError(line, field, $"'{(raw.Length > 30 ? raw[..30] : raw)}' is not a valid {meta2.Type.ToString().ToLowerInvariant()}."));
                    continue;
                }
            }

            values[field] = raw;
        }

        return values;
    }

    /// <summary>Checks a file without importing anything: which columns match, a sample of rows, and the problems found in the first rows.</summary>
    public Task<ImportPreview> PreviewAsync(string doctype, string csv, string mode, IUserContext user, CancellationToken ct = default)
    {
        var meta = Require(doctype, mode, user);
        var (columns, rows) = Parse(meta, csv, mode);
        var errors = new List<ImportRowError>();
        var more = false;
        for (var i = 0; i < Math.Min(rows.Count, PreviewRows); i++)
        {
            var values = RowValues(meta, columns, rows[i], mode, i + 2, errors);
            if (mode == "Insert")
            {
                foreach (var required in ImportableFields(meta).Where(f => f.Required && f.Default is null))
                {
                    if (!values.TryGetValue(required.Name, out var present) || present is null or "")
                        errors.Add(new ImportRowError(i + 2, required.Name, "This field is required."));
                }
            }

            var nameColumn = columns.FindIndex(c => c.Field == "name");
            if (mode == "Update" && (nameColumn < 0 || nameColumn >= rows[i].Count || rows[i][nameColumn].Trim().Length == 0))
                errors.Add(new ImportRowError(i + 2, "name", "Every row needs the ID of the document to update."));
            if (errors.Count > MaxErrors)
            {
                errors.RemoveRange(MaxErrors, errors.Count - MaxErrors);
                more = true;
                break;
            }
        }

        var width = columns.Count;
        var sample = rows.Take(SampleRows).Select(r => (IReadOnlyList<string>)Enumerable.Range(0, width).Select(i => i < r.Count ? Trim(r[i]) : string.Empty).ToList()).ToList();
        return Task.FromResult(new ImportPreview(columns, rows.Count, sample, errors, more));
    }

    private static string Trim(string cell) => cell.Length > 80 ? cell[..80] + "…" : cell;

    /// <summary>Imports the file, row by row, as the person. A permission problem on the first row stops at once.</summary>
    public async Task<ImportResult> RunAsync(string doctype, string csv, string mode, bool stopOnError, IUserContext user, CancellationToken ct = default)
    {
        var meta = Require(doctype, mode, user);
        var (columns, rows) = Parse(meta, csv, mode);
        var nameColumn = columns.FindIndex(c => c.Field == "name");
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

        for (var i = 0; i < rows.Count && !ct.IsCancellationRequested; i++)
        {
            var line = i + 2;
            var rowErrors = new List<ImportRowError>();
            var values = RowValues(meta, columns, rows[i], mode, line, rowErrors);
            if (rowErrors.Count > 0)
            {
                Fail(rowErrors[0]);
                if (stopOnError) { stopped = true; break; }
                continue;
            }

            try
            {
                if (mode == "Insert")
                {
                    values.Remove("name");
                    if (nameColumn >= 0 && rows[i].ElementAtOrDefault(nameColumn)?.Trim() is { Length: > 0 } wanted && meta.Autoname == "prompt")
                        values["name"] = wanted;
                    await documents.InsertAsync(doctype, values, user, ct);
                    created++;
                }
                else
                {
                    var name = rows[i].ElementAtOrDefault(nameColumn)?.Trim() ?? string.Empty;
                    if (name.Length == 0)
                        throw Invalid("name", "Every row needs the ID of the document to update.");
                    values.Remove("name");
                    await documents.UpdateAsync(doctype, name, values, user, ct);
                    updated++;
                }
            }
            catch (DocumentValidationException ex)
            {
                foreach (var e in ex.Errors.Take(3))
                    Fail(new ImportRowError(line, string.IsNullOrEmpty(e.Field) ? null : e.Field, e.Message));
                if (stopOnError) { stopped = true; break; }
            }
            catch (NotFoundException)
            {
                Fail(new ImportRowError(line, "name", mode == "Update" ? "No document has this ID." : "Something this row refers to was not found."));
                if (stopOnError) { stopped = true; break; }
            }
            catch (ConflictException ex)
            {
                Fail(new ImportRowError(line, null, ex.Message));
                if (stopOnError) { stopped = true; break; }
            }
            catch (PermissionDeniedException) when (created + updated == 0)
            {
                throw; // nothing was done and nothing will be: tell the person plainly
            }
            catch (PermissionDeniedException ex)
            {
                Fail(new ImportRowError(line, null, ex.Message));
                if (stopOnError) { stopped = true; break; }
            }
        }

        return new ImportResult(created, updated, failed, errors, stopped, more);
    }
}
