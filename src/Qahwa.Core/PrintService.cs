using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Qahwa.Abstractions;
using Qahwa.Core.Designer;

namespace Qahwa.Core;

public sealed record PrintFormatInfo(string Name, string DocType, bool IsDefault, string? LetterHead);

public sealed record PrintFormatDetail(string Name, string DocType, bool IsDefault, string? LetterHead, string Html);

public sealed record PrintFormatRequest(string? Name = null, string? DocType = null, string? Html = null, bool IsDefault = false, string? LetterHead = null);

public sealed record LetterHeadDetail(string Name, string Header, string Footer, bool IsDefault);

public sealed record LetterHeadRequest(string? Name = null, string? Header = null, string? Footer = null, bool IsDefault = false);

/// <summary>What a person can pick when printing a document of one DocType.</summary>
public sealed record PrintChoices(IReadOnlyList<PrintFormatInfo> Formats, IReadOnlyList<string> LetterHeads);

/// <param name="Nonce">Goes into the page's Content-Security-Policy so only the page's own print button script can run.</param>
public sealed record PrintPage(string Html, string Nonce);

/// <summary>
/// Printing: a document as a page for paper (or "Save as PDF" in the browser). The layout is a Print Format, HTML with
/// <c>{{field}}</c> placeholders (<see cref="PrintTemplate"/>), optionally under a Letter Head (a header and footer). Without
/// a format, a clean layout of the document's fields and tables is made. The document is read with the person's own
/// permissions, so a printout never shows a field they could not see.
/// </summary>
public sealed partial class PrintService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    DocumentService documents,
    PermissionService permissions,
    HistoryRecorder history,
    AccessLogService? accessLog = null)
{
    public const string FormatDocType = "Print Format";
    public const string LetterHeadDocType = "Letter Head";
    public const int MaxFormatsPerDocType = 20;
    public const int MaxLetterHeads = 20;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 _\-().]{0,99}$")]
    private static partial Regex NameRegex();

    private static DocumentValidationException Invalid(string field, string message) => new([new FieldError(field, message)]);

    private bool IsManager(IUserContext user) =>
        permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal);

    private void RequireManager(IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (!IsManager(user))
            throw new PermissionDeniedException("Only a System Manager can manage print formats and letter heads.");
    }

    private DocTypeMeta Printable(string doctype)
    {
        if (!registry.TryGet(doctype, out var meta) || meta.IsChildTable)
            throw new NotFoundException($"DocType '{doctype}' was not found.");
        return meta;
    }

    // ---- choices and rendering ----

    /// <summary>The formats made for a DocType and the letter heads, for the print dialog. Needs read access to the DocType.</summary>
    public async Task<PrintChoices> ChoicesAsync(string doctype, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var meta = Printable(doctype);
        permissions.Require(meta, user, PermAction.Read);

        var formats = await repository.ListAsync(registry.Get(FormatDocType), new ListQuery
        {
            Filters = [new("doc_type", FilterOp.Eq, doctype)], OrderBy = "name", Descending = false, Limit = 100,
        }, ct);
        var heads = await repository.ListAsync(registry.Get(LetterHeadDocType), new ListQuery { Fields = ["name"], OrderBy = "name", Descending = false, Limit = 100 }, ct);
        return new PrintChoices(
            formats.Select(f => new PrintFormatInfo((string)f["name"]!, doctype, f["is_default"] is true, f["letter_head"] as string)).ToList(),
            heads.Select(h => (string)h["name"]!).ToList());
    }

    /// <summary>The page for one document. <paramref name="format"/> and <paramref name="letterHead"/> are names; "none" for no letter head.</summary>
    public async Task<PrintPage> RenderAsync(string doctype, string name, string? format, string? letterHead, IUserContext user, CancellationToken ct = default)
    {
        var doc = await documents.GetAsync(doctype, name, user, ct); // read access, with hidden fields already removed
        var meta = Printable(doctype);
        permissions.Require(meta, user, PermFlag.Print);

        Dictionary<string, object?>? formatRow = null;
        if (!string.IsNullOrWhiteSpace(format))
        {
            formatRow = await repository.GetAsync(registry.Get(FormatDocType), format, ct);
            if (formatRow is null || !string.Equals(formatRow["doc_type"] as string, doctype, StringComparison.OrdinalIgnoreCase))
                throw new NotFoundException($"Print format '{OneLine(format)}' was not found.");
        }
        else
        {
            var defaults = await repository.ListAsync(registry.Get(FormatDocType), new ListQuery
            {
                Filters = [new("doc_type", FilterOp.Eq, doctype), new("is_default", FilterOp.Eq, true)], Limit = 1,
            }, ct);
            formatRow = defaults.FirstOrDefault();
        }

        var head = await LetterHeadAsync(letterHead, formatRow?["letter_head"] as string, ct);
        if (accessLog is not null)
            await accessLog.RecordAsync("Print", doctype, name, 1, user, ct);
        return Wrap(meta, name, formatRow is null ? AutoLayout(meta, doc) : FillIn((string)formatRow["html"]!, meta, name, doc, user), head);
    }

    /// <summary>A format being edited, on a real document, without saving it. System Managers only.</summary>
    public async Task<PrintPage> PreviewAsync(string doctype, string name, string html, string? letterHead, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var meta = Printable(doctype);
        if (Validate(html ?? string.Empty, meta) is { Count: > 0 } errors)
            throw new DocumentValidationException(errors);
        var doc = await documents.GetAsync(doctype, name, user, ct);
        return Wrap(meta, name, FillIn(html!, meta, name, doc, user), await LetterHeadAsync(letterHead, null, ct));
    }

    private async Task<(string Header, string Footer)?> LetterHeadAsync(string? requested, string? fromFormat, CancellationToken ct)
    {
        var meta = registry.Get(LetterHeadDocType);
        if (requested == "none")
            return null;
        var wanted = !string.IsNullOrWhiteSpace(requested) ? requested : fromFormat;
        Dictionary<string, object?>? row = null;
        if (!string.IsNullOrWhiteSpace(wanted))
            row = await repository.GetAsync(meta, wanted, ct) ?? throw new NotFoundException($"Letter head '{OneLine(wanted)}' was not found.");
        else
            row = (await repository.ListAsync(meta, new ListQuery { Filters = [new("is_default", FilterOp.Eq, true)], Limit = 1 }, ct)).FirstOrDefault();
        return row is null ? null : (row["header_html"] as string ?? string.Empty, row["footer_html"] as string ?? string.Empty);
    }

    private static string OneLine(string text) => new string(text.Select(c => char.IsControl(c) ? ' ' : c).Take(80).ToArray());

    private string FillIn(string template, DocTypeMeta meta, string name, IReadOnlyDictionary<string, object?> doc, IUserContext user)
    {
        var status = meta.IsSubmittable ? new[] { "Draft", "Submitted", "Cancelled" }[Math.Clamp(Convert.ToInt32(doc.GetValueOrDefault("docstatus") ?? 0, CultureInfo.InvariantCulture), 0, 2)] : string.Empty;
        var specials = new Dictionary<string, string>
        {
            ["name"] = name, ["doctype"] = meta.Name, ["today"] = DateOnly.FromDateTime(history.Now()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["user"] = user.User, ["status"] = status,
        };
        return PrintTemplate.Render(template, meta, doc, child => registry.TryGet(child, out var m) ? m : null,
            specials);
    }

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>The layout used when no format is chosen: the title, then every field the person can see, then each table.</summary>
    private string AutoLayout(DocTypeMeta meta, IReadOnlyDictionary<string, object?> doc)
    {
        var html = new StringBuilder();
        var title = meta.TitleField is { } t && doc.GetValueOrDefault(t) is { } v ? PrintTemplate.Show(null, v) : (string)doc["name"]!;
        html.Append("<h1>").Append(E(title)).Append("</h1><p class=\"muted\">").Append(E(meta.Name)).Append(' ').Append(E((string)doc["name"]!)).Append("</p>");

        html.Append("<table class=\"fields\"><tbody>");
        foreach (var field in meta.Fields.Where(f => FieldTypes.HasColumn(f.Type) && f.Type is not (FieldType.Password or FieldType.Attach or FieldType.AttachImage) && !f.Hidden))
        {
            if (!doc.TryGetValue(field.Name, out var value) || value is null or "")
                continue;
            html.Append("<tr><th>").Append(E(field.Label ?? field.Name)).Append("</th><td>")
                .Append(field.Type is FieldType.Text or FieldType.LongText or FieldType.SmallText or FieldType.TextEditor ? E(PrintTemplate.Show(field, value)).Replace("\n", "<br>") : E(PrintTemplate.Show(field, value))).Append("</td></tr>");
        }

        html.Append("</tbody></table>");
        foreach (var field in meta.Fields.Where(f => f.Type == FieldType.Table && !f.Hidden))
        {
            if (!registry.TryGet(field.Options ?? string.Empty, out var child) || doc.GetValueOrDefault(field.Name) is not IEnumerable<object?> rows)
                continue;
            var data = rows.OfType<IReadOnlyDictionary<string, object?>>().Take(DocumentService.MaxRows).ToList();
            if (data.Count == 0)
                continue;
            var columns = child.Fields.Where(f => FieldTypes.HasColumn(f.Type) && f.Type is not (FieldType.Password or FieldType.Attach or FieldType.AttachImage) && !f.Hidden).ToList();
            html.Append("<h2>").Append(E(field.Label ?? field.Name)).Append("</h2><table class=\"rows\"><thead><tr><th>#</th>");
            foreach (var column in columns)
                html.Append("<th>").Append(E(column.Label ?? column.Name)).Append("</th>");
            html.Append("</tr></thead><tbody>");
            var n = 0;
            foreach (var row in data)
            {
                html.Append("<tr><td>").Append(++n).Append("</td>");
                foreach (var column in columns)
                    html.Append("<td>").Append(E(PrintTemplate.Show(column, row.GetValueOrDefault(column.Name)))).Append("</td>");
                html.Append("</tr>");
            }

            html.Append("</tbody></table>");
        }

        return html.ToString();
    }

    private const string BaseCss = """
        body{font:14px/1.5 system-ui,-apple-system,'Segoe UI',Roboto,'Noto Sans Arabic',sans-serif;color:#1a1a1a;margin:0;padding:24px;background:#fff}
        h1{font-size:22px;margin:0 0 2px}h2{font-size:16px;margin:22px 0 6px}.muted{color:#666;margin:0 0 14px}
        table{border-collapse:collapse;width:100%}th,td{text-align:start;padding:5px 8px;vertical-align:top}
        .fields th{width:30%;color:#555;font-weight:600;border-bottom:1px solid #eee}.fields td{border-bottom:1px solid #eee}
        .rows th{background:#f3f3f3;border:1px solid #ddd}.rows td{border:1px solid #ddd}
        .lh-header{border-bottom:2px solid #222;margin-bottom:16px;padding-bottom:8px}.lh-footer{border-top:1px solid #999;margin-top:24px;padding-top:8px;font-size:12px;color:#555}
        .no-print{position:fixed;top:8px;inset-inline-end:8px}.no-print button{font:inherit;padding:6px 14px;cursor:pointer}
        @media print{body{padding:0}.no-print{display:none}}
        """;

    private static PrintPage Wrap(DocTypeMeta meta, string name, string body, (string Header, string Footer)? head)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var html = new StringBuilder();
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>")
            .Append(E($"{meta.Name} {name}")).Append("</title><style>").Append(BaseCss).Append("</style></head><body>")
            .Append("<div class=\"no-print\"><button id=\"print-btn\" type=\"button\">Print</button></div>");
        if (head is { Header.Length: > 0 })
            html.Append("<div class=\"lh-header\">").Append(head.Value.Header).Append("</div>");
        html.Append(body);
        if (head is { Footer.Length: > 0 })
            html.Append("<div class=\"lh-footer\">").Append(head.Value.Footer).Append("</div>");
        html.Append("<script nonce=\"").Append(nonce).Append("\">document.getElementById('print-btn').addEventListener('click',function(){window.print()})</script></body></html>");
        return new PrintPage(html.ToString(), nonce);
    }

    // ---- managing formats ----

    private List<FieldError> Validate(string html, DocTypeMeta meta) => PrintTemplate.Validate(html, meta, registry);

    public async Task<IReadOnlyList<PrintFormatInfo>> ListFormatsAsync(IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var rows = await repository.ListAsync(registry.Get(FormatDocType), new ListQuery { OrderBy = "name", Descending = false, Limit = 500 }, ct);
        return rows.Select(f => new PrintFormatInfo((string)f["name"]!, (string)f["doc_type"]!, f["is_default"] is true, f["letter_head"] as string)).ToList();
    }

    public async Task<PrintFormatDetail> GetFormatAsync(string name, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var row = await repository.GetAsync(registry.Get(FormatDocType), name, ct) ?? throw new NotFoundException("Print format not found.");
        return new PrintFormatDetail(name, (string)row["doc_type"]!, row["is_default"] is true, row["letter_head"] as string, (string)row["html"]!);
    }

    public async Task<PrintFormatDetail> SaveFormatAsync(PrintFormatRequest request, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var name = (request.Name ?? string.Empty).Trim();
        if (!NameRegex().IsMatch(name))
            throw Invalid("name", "Name the format with up to 100 letters, digits, spaces and - _ ( ) .");
        var meta = Printable((request.DocType ?? string.Empty).Trim());
        if (meta.Module == "Core")
            throw Invalid("docType", "Choose one of your own DocTypes.");
        if (Validate(request.Html ?? string.Empty, meta) is { Count: > 0 } errors)
            throw new DocumentValidationException(errors);
        var letterHead = string.IsNullOrWhiteSpace(request.LetterHead) ? null : request.LetterHead.Trim();
        if (letterHead is not null && !await repository.ExistsAsync(registry.Get(LetterHeadDocType), letterHead, ct))
            throw Invalid("letterHead", $"'{OneLine(letterHead)}' is not a letter head.");

        var formats = registry.Get(FormatDocType);
        var existing = await repository.GetAsync(formats, name, ct);
        if (existing is not null && !string.Equals(existing["doc_type"] as string, meta.Name, StringComparison.Ordinal))
            throw Invalid("name", "Another format with this name exists for a different DocType.");
        var sameDocType = await repository.ListAsync(formats, new ListQuery { Filters = [new("doc_type", FilterOp.Eq, meta.Name)], Fields = ["name", "is_default"], Limit = MaxFormatsPerDocType + 1 }, ct);
        if (existing is null && sameDocType.Count >= MaxFormatsPerDocType)
            throw Invalid("name", $"A DocType can have at most {MaxFormatsPerDocType} print formats.");

        var now = history.Now();
        if (existing is null)
        {
            var row = HistoryRecorder.Standard(user.User, now);
            row["name"] = name;
            row["format_name"] = name;
            row["doc_type"] = meta.Name;
            row["html"] = request.Html;
            row["is_default"] = request.IsDefault;
            row["letter_head"] = letterHead;
            await repository.InsertAsync(formats, row, null, ct);
        }
        else
        {
            var stored = (DateTime)existing["modified"]!;
            var changes = new Dictionary<string, object?>
            {
                ["html"] = request.Html, ["is_default"] = request.IsDefault, ["letter_head"] = letterHead,
                ["modified"] = now > stored ? now : stored.AddTicks(10), ["modified_by"] = user.User,
            };
            if (!await repository.UpdateAsync(formats, name, changes, stored, null, ct))
                throw new ConflictException("This format was changed by someone else. Reload and try again.");
        }

        if (request.IsDefault)
        {
            foreach (var other in sameDocType.Where(f => f["is_default"] is true && (string)f["name"]! != name))
                await repository.UpdateAsync(formats, (string)other["name"]!, new Dictionary<string, object?> { ["is_default"] = false, ["modified"] = history.Now() }, null, null, ct);
        }

        return await GetFormatAsync(name, user, ct);
    }

    public async Task DeleteFormatAsync(string name, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        if (!await repository.DeleteAsync(registry.Get(FormatDocType), name, null, ct))
            throw new NotFoundException("Print format not found.");
    }

    // ---- letter heads ----

    public async Task<IReadOnlyList<LetterHeadDetail>> ListLetterHeadsAsync(IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var rows = await repository.ListAsync(registry.Get(LetterHeadDocType), new ListQuery { OrderBy = "name", Descending = false, Limit = 200 }, ct);
        return rows.Select(h => new LetterHeadDetail((string)h["name"]!, h["header_html"] as string ?? string.Empty, h["footer_html"] as string ?? string.Empty, h["is_default"] is true)).ToList();
    }

    public async Task<LetterHeadDetail> SaveLetterHeadAsync(LetterHeadRequest request, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var name = (request.Name ?? string.Empty).Trim();
        if (!NameRegex().IsMatch(name))
            throw Invalid("name", "Name the letter head with up to 100 letters, digits, spaces and - _ ( ) .");
        var header = request.Header ?? string.Empty;
        var footer = request.Footer ?? string.Empty;
        var errors = new List<FieldError>();
        if (PrintTemplate.UnsafeReason(header) is { } h)
            errors.Add(new FieldError("header", h));
        if (PrintTemplate.UnsafeReason(footer) is { } f)
            errors.Add(new FieldError("footer", f));
        if (errors.Count > 0)
            throw new DocumentValidationException(errors);

        var heads = registry.Get(LetterHeadDocType);
        var all = await repository.ListAsync(heads, new ListQuery { Fields = ["name", "is_default"], Limit = MaxLetterHeads + 1 }, ct);
        var existing = all.FirstOrDefault(r => (string)r["name"]! == name);
        if (existing is null && all.Count >= MaxLetterHeads)
            throw Invalid("name", $"At most {MaxLetterHeads} letter heads.");

        var now = history.Now();
        if (existing is null)
        {
            var row = HistoryRecorder.Standard(user.User, now);
            row["name"] = name;
            row["letter_head_name"] = name;
            row["header_html"] = header;
            row["footer_html"] = footer;
            row["is_default"] = request.IsDefault;
            await repository.InsertAsync(heads, row, null, ct);
        }
        else
        {
            var stored = (await repository.GetAsync(heads, name, ct))!["modified"] as DateTime? ?? now;
            await repository.UpdateAsync(heads, name, new Dictionary<string, object?>
            {
                ["header_html"] = header, ["footer_html"] = footer, ["is_default"] = request.IsDefault,
                ["modified"] = now > stored ? now : stored.AddTicks(10), ["modified_by"] = user.User,
            }, null, null, ct);
        }

        if (request.IsDefault)
        {
            foreach (var other in all.Where(r => r["is_default"] is true && (string)r["name"]! != name))
                await repository.UpdateAsync(heads, (string)other["name"]!, new Dictionary<string, object?> { ["is_default"] = false, ["modified"] = history.Now() }, null, null, ct);
        }

        return new LetterHeadDetail(name, header, footer, request.IsDefault);
    }

    public async Task DeleteLetterHeadAsync(string name, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        if (!await repository.DeleteAsync(registry.Get(LetterHeadDocType), name, null, ct))
            throw new NotFoundException("Letter head not found.");
    }
}
