namespace Qahwa.Core;

/// <summary>A client script as sent to the browser.</summary>
public sealed record ClientScriptInfo(string Name, string Title, string Source);

/// <summary>
/// Client Scripts: JavaScript an administrator attaches to a DocType's form. They run in the browser, in a sandbox of their own (a worker
/// with no page, no network and no storage), and can only ask the form for a few things (see ADR-057). This service hands the scripts
/// of a DocType to people who may open that DocType; the Client Script DocType itself is for System Managers only.
/// </summary>
public sealed class ClientScriptService(IMetaRegistry registry, IDocumentRepository repository, PermissionService permissions)
{
    public const string DocTypeName = "Client Script";
    public const int MaxPerDocType = 10;

    public async Task<IReadOnlyList<ClientScriptInfo>> ForAsync(string doctype, IUserContext user, CancellationToken ct = default)
    {
        var meta = registry.Get(doctype);
        permissions.Require(meta, user, PermAction.Read); // someone who cannot open the DocType gets nothing about it
        var rows = await repository.ListAsync(registry.Get(DocTypeName), new ListQuery
        {
            Filters = [new QueryFilter("active", FilterOp.Eq, true), new QueryFilter("reference_doctype", FilterOp.Eq, meta.Name)],
            Fields = ["name", "title", "script"], OrderBy = "creation", Descending = false, Limit = MaxPerDocType,
        }, ct);
        return rows.Where(r => r["script"] is string { Length: > 0 and <= ScriptSandbox.MaxSourceLength })
            .Select(r => new ClientScriptInfo((string)r["name"]!, r["title"] as string ?? string.Empty, (string)r["script"]!)).ToList();
    }

    /// <summary>Checks the source of a Client Script being saved.</summary>
    public static void Check(IReadOnlyDictionary<string, object?> values, List<FieldError> errors)
    {
        if (values.GetValueOrDefault("script") is string source && ScriptSandbox.SyntaxProblem(source) is { } problem)
            errors.Add(new FieldError("script", problem));
    }
}
