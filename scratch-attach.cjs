const fs = require('fs')
function edIn(root, f, a, b) {
  const p = root + f
  let s = fs.readFileSync(p, 'utf8')
  if (!s.includes(a)) {
    s = s.split('\r\n').join('\n')
    if (!s.includes(a)) throw new Error(f + ' missing ' + a)
  }
  fs.writeFileSync(p, s.replace(a, b))
}
const S = 'D:/Me/Qahwa/src/'
const D = 'D:/Me/Qahwa/src/Qahwa.Desk/src/'

// ---- server ----
edIn(S, 'Qahwa.Core/Files.cs', `    /// <summary>Removes a file: by whoever uploaded it`, `    /// <summary>
    /// Whether a file may be named in an Attach or Image field of one document: it must exist, be attached to that very document (so a field
    /// can never point at someone else's file) and, for an Image field, be a real picture. Returns what is wrong, or null.
    /// </summary>
    public async Task<string?> CheckFieldFileAsync(string fileId, string doctype, string? name, bool requireImage, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(name))
            return "Save the document first, then attach the file.";
        var row = fileId.Length > FieldTypes.ShortTextLength ? null : await repository.GetAsync(Meta, fileId, ct);
        if (row is null)
            return "That file does not exist. Upload it to this document first.";
        if (!string.Equals(row["reference_doctype"] as string, doctype, StringComparison.Ordinal) || !string.Equals(row["reference_name"] as string, name, StringComparison.Ordinal))
            return "That file is attached to another document.";
        return requireImage && !IsInlineImage(row["content_type"] as string ?? string.Empty) ? "This field needs a picture (PNG, JPEG, GIF or WebP)." : null;
    }

    /// <summary>A file is removed: any Attach or Image field of its document that named it is emptied, so nothing points at a file that is gone.</summary>
    private async Task ClearFieldsNamingAsync(string fileId, string doctype, string name, CancellationToken ct)
    {
        if (!registry.TryGet(doctype, out var meta))
            return;
        var fields = meta.Fields.Where(f => f.Type is FieldType.Attach or FieldType.AttachImage).Select(f => f.Name).ToList();
        if (fields.Count == 0 || await repository.GetAsync(meta, name, ct) is not { } doc)
            return;

        var changes = fields.Where(f => string.Equals(doc.GetValueOrDefault(f) as string, fileId, StringComparison.Ordinal)).ToDictionary(f => f, _ => (object?)null);
        if (changes.Count == 0)
            return;
        var stored = (DateTime)doc["modified"]!;
        changes["modified"] = history.Now() > stored ? history.Now() : stored.AddTicks(10);
        await repository.UpdateAsync(meta, name, changes, null, null, ct);
    }

    /// <summary>Removes a file: by whoever uploaded it`)
edIn(S, 'Qahwa.Core/Files.cs', `        await repository.DeleteAsync(Meta, id, null, ct);
        await storage.DeleteAsync((string)row["storage_key"]!, ct);
        await NoteAsync(doctype, name, $"Removed {row["file_name"]}", user.User, ct);`, `        await repository.DeleteAsync(Meta, id, null, ct);
        await storage.DeleteAsync((string)row["storage_key"]!, ct);
        await ClearFieldsNamingAsync(id, doctype, name, ct);
        await NoteAsync(doctype, name, $"Removed {row["file_name"]}", user.User, ct);`)
edIn(S, 'Qahwa.Core/DocumentService.cs', `            if (field.Type == FieldType.Link && field.Options == DocTypeTarget && value is string named)`, `            if (field.Type is FieldType.Attach or FieldType.AttachImage && value is string fileId)
            {
                // Files are attached to a saved document first; the field then names one of them.
                var problem = pathPrefix.Length > 0 ? "Attachments cannot be inside a table."
                    : files is null ? null
                    : await files.Value.CheckFieldFileAsync(fileId, meta.Name, document.GetValueOrDefault("name") as string, field.Type == FieldType.AttachImage, ct);
                if (problem is not null)
                    errors.Add(new FieldError(path, problem));
            }
            else if (field.Type == FieldType.Link && field.Options == DocTypeTarget && value is string named)`)
edIn(S, 'Qahwa.Core/Designer/FieldTypeCatalog.cs', `new(FieldType.Attach, "Attachment", "Advanced", "An uploaded file", OptionsKind.None, false, Later),
        new(FieldType.AttachImage, "Image", "Advanced", "An uploaded image", OptionsKind.None, false, Later),`, `new(FieldType.Attach, "Attachment", "Advanced", "An uploaded file (attach it after the document is saved)", OptionsKind.None, true),
        new(FieldType.AttachImage, "Image", "Advanced", "An uploaded picture, shown on the form", OptionsKind.None, true),`)
edIn(S, 'Qahwa.Core/Print.cs', `                case FieldType.Signature:
                    return "(signed)"; // the picture itself is never put on a printout`, `                case FieldType.Signature:
                    return "(signed)"; // the picture itself is never put on a printout
                case FieldType.Attach or FieldType.AttachImage:
                    return value is string { Length: > 0 } ? "(attached)" : string.Empty; // a file id means nothing on paper`)
edIn(S, 'Qahwa.Api/OpenApi.cs', `            FieldType.Signature =>`, `            FieldType.Attach or FieldType.AttachImage => new Dictionary<string, object?> { ["type"] = "string", ["description"] = "The id of a file attached to this document (upload it with /api/files/for/{doctype}/{name} first)." },
            FieldType.Signature =>`)

// ---- Desk ----
fs.writeFileSync(D + 'controls/DocContext.tsx', `import { createContext, useContext } from 'react'

/** Which document the form is showing, for fields that need to know (an attachment is uploaded to the document). */
export interface DocScope {
  doctype: string
  /** The saved document's name; null while it is still new. */
  name: string | null
}

export const DocContext = createContext<DocScope | null>(null)

export const useDocScope = (): DocScope | null => useContext(DocContext)
`)
edIn(D, 'lib/fields.ts', `export const isEditable = (type: FieldType) =>
  hasColumn(type) && type !== 'Password' && type !== 'Attach' && type !== 'AttachImage'`, `export const isEditable = (type: FieldType) => hasColumn(type) && type !== 'Password'`)
edIn(D, 'lib/fields.ts', `    case 'Rating': return starsLabel(value)`, `    case 'Attach': case 'AttachImage': return '(file)'
    case 'Rating': return starsLabel(value)`)
edIn(D, 'controls/FieldControl.tsx', `    case 'Rating': return <RatingInput`, `    case 'Attach': case 'AttachImage': return <AttachInput id={id} ariaLabel={ariaLabel} value={value} readOnly={readOnly} invalid={invalid} image={field.type === 'AttachImage'} onChange={onChange} />
    case 'Rating': return <RatingInput`)
edIn(D, 'controls/FieldControl.tsx', `import { ColorInput, DurationInput,`, `import { AttachInput, ColorInput, DurationInput,`)
edIn(D, 'pages/FormView.tsx', `      <form className="form-stack" onSubmit={(e) => { e.preventDefault(); if (dirty && editable) save.mutate() }}>`, `      <DocContext.Provider value={{ doctype: meta.name, name: doc?.name ?? null }}>
      <form className="form-stack" onSubmit={(e) => { e.preventDefault(); if (dirty && editable) save.mutate() }}>`)
edIn(D, 'pages/FormView.tsx', `      </form>
      </div>`, `      </form>
      </DocContext.Provider>
      </div>`)
edIn(D, 'pages/FormView.tsx', `import { fromInput, hasColumn,`, `import { DocContext } from '../controls/DocContext.tsx'
import { fromInput, hasColumn,`)
fs.appendFileSync(D + 'controls/SpecialInputs.tsx', `
/**
 * A file field. The file is uploaded to the saved document (the same place as the side panel) and the field keeps its id, so a new
 * document must be saved first. A picture is shown on the form; any other file is a download link.
 */
export function AttachInput({ value, onChange, readOnly, id, ariaLabel, image }: Common & { image: boolean }) {
  const scope = useDocScope()
  const queryClient = useQueryClient()
  const fileId = typeof value === 'string' && value !== '' ? value : null
  const files = useQuery({
    queryKey: ['files', scope?.doctype, scope?.name],
    queryFn: () => api.files.list(scope!.doctype, scope!.name!),
    enabled: Boolean(scope?.name) && fileId !== null,
    retry: false,
  })
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const chosen = files.data?.find((f) => f.id === fileId)

  if (!scope?.name) return <small className="muted">Save the document first, then attach the file.</small>

  const choose = async (file: File | undefined) => {
    if (!file) return
    const problem = fileProblem(file)
    if (problem) { setError(problem); return }
    if (image && !file.type.startsWith('image/')) { setError('Choose a picture (PNG, JPEG, GIF or WebP).'); return }
    setBusy(true)
    setError(null)
    try {
      const uploaded = await api.files.upload(scope.doctype, scope.name!, file)
      onChange(uploaded.id)
      await queryClient.invalidateQueries({ queryKey: ['files', scope.doctype, scope.name] })
    } catch (e) {
      setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Could not upload the file.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div id={id} className="attach-input" role="group" aria-label={ariaLabel}>
      {fileId && chosen?.image && <img src={api.files.url(fileId)} alt={chosen.fileName} className="attach-preview" />}
      {fileId && <a href={api.files.url(fileId)} download>{chosen?.fileName ?? 'Download the file'}</a>}
      {!readOnly && (
        <>
          <input type="file" aria-label={fileId ? 'Replace the file' : 'Choose a file'} accept={image ? 'image/png,image/jpeg,image/gif,image/webp' : undefined} disabled={busy} onChange={(e) => { void choose(e.target.files?.[0]); e.target.value = '' }} />
          {fileId && <button type="button" className="btn small" onClick={() => onChange('')}>Remove from this field</button>}
        </>
      )}
      {busy && <small className="muted" role="status">Uploading…</small>}
      {error && <small className="error-text" role="alert">{error}</small>}
    </div>
  )
}
`)
edIn(D, 'controls/SpecialInputs.tsx', `import { useEffect, useRef, useState } from 'react'`, `import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import { fileProblem } from '../lib/files.ts'
import { useDocScope } from './DocContext.tsx'`)
fs.appendFileSync(D + 'styles.css', `
.attach-input { display: grid; gap: .4rem; justify-items: start; }
.attach-preview { max-inline-size: 16rem; max-block-size: 12rem; border-radius: 8px; border: 1px solid var(--border, #ccc); object-fit: contain; background: #fff; }
`)
console.log('ok')
