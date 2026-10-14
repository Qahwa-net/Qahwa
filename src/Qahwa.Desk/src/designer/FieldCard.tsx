import type { DocPerm, FieldTypeInfo } from '../api/types.ts'
import { isLayoutType, isTextType, slugify, uniqueName, type DraftField } from '../lib/designer.ts'

interface Props {
  field: DraftField
  index: number
  total: number
  expanded: boolean
  info: FieldTypeInfo | undefined
  otherNames: string[]
  doctypes: string[]
  errors: Record<string, string>
  disabled: boolean
  onToggle: () => void
  onChange: (patch: Partial<DraftField>) => void
  onMove: (delta: number) => void
  onRemove: () => void
}

function Err({ message }: { message?: string }) {
  return message ? <small className="error-text" role="alert">{message}</small> : null
}

export function FieldCard({
  field, index, total, expanded, info, otherNames, doctypes, errors, disabled, onToggle, onChange, onMove, onRemove,
}: Props) {
  const path = `fields[${index}]`
  const layout = isLayoutType(field.type)
  const hasError = Object.keys(errors).some((k) => k.startsWith(`${path}.`))
  const choices = (field.options ?? '').split('\n').map((c) => c.trim()).filter(Boolean)

  function setLabel(label: string) {
    onChange({
      label,
      ...(field.nameFollowsLabel && !field.locked
        ? { name: uniqueName(slugify(label) || 'field', otherNames) }
        : {}),
    })
  }

  return (
    <li className={`field-card${expanded ? ' open' : ''}${hasError ? ' has-error' : ''}`}>
      <div className="field-card-head">
        <button type="button" className="field-card-title" aria-expanded={expanded} onClick={onToggle}>
          <strong>{field.label || field.name}</strong>
          <span className="muted"> · {info?.label ?? field.type}</span>
          {field.required && <span className="req"> *</span>}
          {field.locked && <span className="badge">saved</span>}
          {hasError && <span className="error-text"> · needs attention</span>}
        </button>
        <span className="field-card-actions">
          <button type="button" className="btn small icon" aria-label={`Move ${field.name} up`}
            disabled={disabled || index === 0} onClick={() => onMove(-1)}>↑</button>
          <button type="button" className="btn small icon" aria-label={`Move ${field.name} down`}
            disabled={disabled || index === total - 1} onClick={() => onMove(1)}>↓</button>
          <button type="button" className="btn small icon danger" aria-label={`Remove ${field.name}`}
            disabled={disabled} onClick={onRemove}>✕</button>
        </span>
      </div>

      {expanded && (
        <div className="field-card-body">
          <label className="field">
            <span>{layout ? 'Heading' : 'Label'}</span>
            <input value={field.label ?? ''} disabled={disabled} onChange={(e) => setLabel(e.target.value)} />
            <Err message={errors[`${path}.label`]} />
          </label>

          <label className="field">
            <span>Field name</span>
            <input value={field.name} disabled={disabled || field.locked} className="mono"
              onChange={(e) => onChange({ name: e.target.value.toLowerCase().replace(/\s+/g, '_'), nameFollowsLabel: false })} />
            <small className="muted">
              {field.locked ? 'The name and type of a saved field cannot change.' : 'Used by the database and the API. Lowercase letters, digits and underscores.'}
            </small>
            <Err message={errors[`${path}.name`]} />
          </label>

          {info?.options === 'Choices' && (
            <label className="field full">
              <span>Choices (one per line)</span>
              <textarea rows={4} disabled={disabled} value={(field.options ?? '').replace(/\r/g, '')}
                onChange={(e) => onChange({ options: e.target.value })} />
              <Err message={errors[`${path}.options`]} />
            </label>
          )}

          {info?.options === 'DocType' && (
            <label className="field full">
              <span>Link to DocType</span>
              <select value={field.options ?? ''} disabled={disabled} onChange={(e) => onChange({ options: e.target.value })}>
                <option value="">Choose…</option>
                {doctypes.map((d) => <option key={d} value={d}>{d}</option>)}
              </select>
              <Err message={errors[`${path}.options`]} />
            </label>
          )}

          {!layout && (
            <>
              <fieldset className="field full flags" disabled={disabled}>
                <legend>Behaviour</legend>
                <label><input type="checkbox" checked={field.required} onChange={(e) => onChange({ required: e.target.checked })} /> Required</label>
                <label><input type="checkbox" checked={field.unique} onChange={(e) => onChange({ unique: e.target.checked })} /> Unique</label>
                <label><input type="checkbox" checked={field.inListView} onChange={(e) => onChange({ inListView: e.target.checked })} /> Show in list</label>
                <label><input type="checkbox" checked={Boolean(field.hidden)} onChange={(e) => onChange({ hidden: e.target.checked })} /> Hidden</label>
                <label><input type="checkbox" checked={Boolean(field.readOnly)} onChange={(e) => onChange({ readOnly: e.target.checked })} /> Read only</label>
                {isTextType(field.type) && (
                  <label><input type="checkbox" checked={field.lowercase} onChange={(e) => onChange({ lowercase: e.target.checked })} /> Lower case</label>
                )}
              </fieldset>

              <label className="field">
                <span>Default value</span>
                {field.type === 'Check' ? (
                  <select value={field.default ?? ''} disabled={disabled} onChange={(e) => onChange({ default: e.target.value || null })}>
                    <option value="">None</option><option value="1">Checked</option><option value="0">Unchecked</option>
                  </select>
                ) : field.type === 'Select' ? (
                  <select value={field.default ?? ''} disabled={disabled} onChange={(e) => onChange({ default: e.target.value || null })}>
                    <option value="">None</option>
                    {choices.map((c) => <option key={c} value={c}>{c}</option>)}
                  </select>
                ) : (
                  <input value={field.default ?? ''} disabled={disabled} onChange={(e) => onChange({ default: e.target.value })} />
                )}
                <Err message={errors[`${path}.default`]} />
              </label>

              <label className="field">
                <span>Help text</span>
                <input value={field.description ?? ''} disabled={disabled} onChange={(e) => onChange({ description: e.target.value })} />
                <Err message={errors[`${path}.description`]} />
              </label>
            </>
          )}
          <Err message={errors[`${path}.type`]} />
        </div>
      )}
    </li>
  )
}

export function PermissionsEditor({ permissions, roles, disabled, errors, onChange }: {
  permissions: DocPerm[]
  roles: string[]
  disabled: boolean
  errors: Record<string, string>
  onChange: (next: DocPerm[]) => void
}) {
  const flags = ['read', 'write', 'create', 'delete', 'ifOwner'] as const
  const labels = { read: 'Read', write: 'Write', create: 'Create', delete: 'Delete', ifOwner: 'Own only' } as const

  const update = (index: number, patch: Partial<DocPerm>) =>
    onChange(permissions.map((p, i) => (i === index ? { ...p, ...patch } : p)))

  return (
    <div>
      <datalist id="role-list">{roles.map((r) => <option key={r} value={r} />)}</datalist>
      <div className="table-wrap">
        <table className="perm-table">
          <thead><tr><th>Role</th>{flags.map((f) => <th key={f}>{labels[f]}</th>)}<th /></tr></thead>
          <tbody>
            {permissions.map((p, i) => (
              <tr key={i}>
                <td>
                  <input list="role-list" aria-label="Role" value={p.role} disabled={disabled}
                    onChange={(e) => update(i, { role: e.target.value })} />
                  {errors[`permissions[${i}].role`] && <small className="error-text" role="alert">{errors[`permissions[${i}].role`]}</small>}
                </td>
                {flags.map((f) => (
                  <td key={f}>
                    <input type="checkbox" aria-label={`${labels[f]} for ${p.role || 'role'}`} checked={p[f]} disabled={disabled}
                      onChange={(e) => update(i, { [f]: e.target.checked })} />
                  </td>
                ))}
                <td><button type="button" className="btn small icon danger" aria-label="Remove rule" disabled={disabled}
                  onClick={() => onChange(permissions.filter((_, n) => n !== i))}>✕</button></td>
              </tr>
            ))}
            {permissions.length === 0 && <tr><td colSpan={7} className="empty">No rules: only Administrators can use this DocType.</td></tr>}
          </tbody>
        </table>
      </div>
      <button type="button" className="btn small btn-gap" disabled={disabled}
        onClick={() => onChange([...permissions, { role: '', read: true, write: false, create: false, delete: false, ifOwner: false }])}>
        + Add rule
      </button>
    </div>
  )
}
