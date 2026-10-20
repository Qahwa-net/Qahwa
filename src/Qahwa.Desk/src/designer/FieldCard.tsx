import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client.ts'
import type { DocPerm, FieldTypeInfo } from '../api/types.ts'
import { localExpressionError } from '../lib/expr/check.ts'
import { ExpressionInput } from './ExpressionInput.tsx'
import { isLayoutType, isTextType, slugify, uniqueName, type DraftField } from '../lib/designer.ts'

interface Props {
  field: DraftField
  index: number
  total: number
  expanded: boolean
  info: FieldTypeInfo | undefined
  otherNames: string[]
  /** DocTypes a Link field may point at. */
  doctypes: string[]
  /** Child DocTypes a Table field may use. */
  childTables: string[]
  /** The DocType being designed is itself a child table. */
  inChildTable: boolean
  /** The DocType is submittable, so a field may be marked as editable after submit. */
  submittable?: boolean
  /** Every field of the DocType being designed: what its expressions may read. */
  fieldNames: string[]
  /** The Link fields of the DocType being designed, for "fetch from". */
  linkFields: DraftField[]
  errors: Record<string, string>
  disabled: boolean
  onToggle: () => void
  onChange: (patch: Partial<DraftField>) => void
  onMove: (delta: number) => void
  onRemove: () => void
}

/** "Fetch from": pick a Link field of this DocType, then a field of the DocType it points at. */
function FetchFromEditor({ field, linkFields, disabled, error, onChange }: {
  field: DraftField
  linkFields: DraftField[]
  disabled: boolean
  error?: string
  onChange: (patch: Partial<DraftField>) => void
}) {
  const [linkName = '', targetName = ''] = (field.fetchFrom ?? '').split('.')
  const link = linkFields.find((f) => f.name === linkName)
  const target = useQuery({
    queryKey: ['designer', 'detail', link?.options],
    queryFn: () => api.designer.get(link!.options!),
    enabled: Boolean(link?.options),
    retry: false,
  })
  const targetFields = [
    ...(target.data?.definition.fields ?? []).filter((f) => f.type !== 'Table' && f.type !== 'SectionBreak' && f.type !== 'TabBreak' && f.type !== 'ColumnBreak'),
  ]

  const set = (link: string, target: string) => onChange({ fetchFrom: link ? `${link}.${target}` : null, fetchIfEmpty: link ? field.fetchIfEmpty : false })

  return (
    <div className="field full">
      <span>Fetch from a linked document</span>
      <span className="inline">
        <select aria-label="Link field to fetch from" value={linkName} disabled={disabled}
          onChange={(e) => set(e.target.value, '')}>
          <option value="">Not fetched</option>
          {linkFields.filter((f) => f.name !== field.name).map((f) => <option key={f.name} value={f.name}>{f.label || f.name}</option>)}
        </select>
        {linkName && (
          <select aria-label="Field to copy" value={targetName} disabled={disabled} onChange={(e) => set(linkName, e.target.value)}>
            <option value="">Choose a field…</option>
            {targetFields.map((f) => <option key={f.name} value={f.name}>{f.label || f.name}</option>)}
            {['name', 'owner', 'creation', 'modified'].map((n) => <option key={n} value={n}>{n} (standard)</option>)}
          </select>
        )}
      </span>
      {linkName && (
        <label className="check-line plain">
          <input type="checkbox" checked={field.fetchIfEmpty ?? false} disabled={disabled}
            onChange={(e) => onChange({ fetchIfEmpty: e.target.checked })} /> Only when this field is still empty
        </label>
      )}
      <small className="muted">The value is copied when the link changes. Nothing is copied if the person may not read the linked document.</small>
      {error && <small className="error-text" role="alert">{error}</small>}
    </div>
  )
}

function Err({ message }: { message?: string }) {
  return message ? <small className="error-text" role="alert">{message}</small> : null
}

export function FieldCard({
  field, index, total, expanded, info, otherNames, doctypes, childTables, inChildTable, submittable, fieldNames, linkFields, errors, disabled,
  onToggle, onChange, onMove, onRemove,
}: Props) {
  const path = `fields[${index}]`
  const layout = isLayoutType(field.type)
  const isTable = field.type === 'Table'
  const targets = isTable ? childTables : doctypes
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
              <span>{isTable ? 'Child table DocType' : 'Link to DocType'}</span>
              <select value={field.options ?? ''} disabled={disabled} onChange={(e) => onChange({ options: e.target.value })}>
                <option value="">Choose…</option>
                {targets.map((d) => <option key={d} value={d}>{d}</option>)}
              </select>
              {isTable && targets.length === 0 && (
                <small className="muted">There is no child table yet. Create a DocType and tick "This DocType is a table used inside other documents".</small>
              )}
              <Err message={errors[`${path}.options`]} />
            </label>
          )}

          {!layout && (
            <>
              <fieldset className="field full flags" disabled={disabled}>
                <legend>Behaviour</legend>
                <label><input type="checkbox" checked={field.required} onChange={(e) => onChange({ required: e.target.checked })} /> {isTable ? 'Needs at least one row' : 'Required'}</label>
                {!isTable && !inChildTable && (
                  <label><input type="checkbox" checked={field.unique} onChange={(e) => onChange({ unique: e.target.checked })} /> Unique</label>
                )}
                {!isTable && (
                  <label><input type="checkbox" checked={field.inListView} onChange={(e) => onChange({ inListView: e.target.checked })} /> {inChildTable ? 'Show as a column' : 'Show in list'}</label>
                )}
                <label><input type="checkbox" checked={Boolean(field.hidden)} onChange={(e) => onChange({ hidden: e.target.checked })} /> Hidden</label>
                <label><input type="checkbox" checked={Boolean(field.readOnly)} onChange={(e) => onChange({ readOnly: e.target.checked })} /> Read only</label>
                {isTextType(field.type) && (
                  <label><input type="checkbox" checked={field.lowercase} onChange={(e) => onChange({ lowercase: e.target.checked })} /> Lower case</label>
                )}
                {!isTable && (
                  <label><input type="checkbox" checked={Boolean(field.setOnce)} onChange={(e) => onChange({ setOnce: e.target.checked })} /> Set only once</label>
                )}
                {['Int', 'Float', 'Decimal', 'Currency', 'Percent'].includes(field.type) && (
                  <label><input type="checkbox" checked={Boolean(field.nonNegative)} onChange={(e) => onChange({ nonNegative: e.target.checked })} /> Not below zero</label>
                )}
                {isTextType(field.type) && field.type !== 'Password' && (
                  <label>Longest text <input type="number" min={1} max={10000} aria-label="Longest text" value={field.maxLength ?? ''} onChange={(e) => onChange({ maxLength: e.target.value === '' ? null : Number(e.target.value) })} /></label>
                )}
              </fieldset>

              {!isTable && <label className="field">
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
              </label>}

              {!inChildTable && <label className="field">
                <span>Permission level</span>
                <select value={field.permLevel ?? 0} disabled={disabled} onChange={(e) => onChange({ permLevel: Number(e.target.value) })}>
                  {Array.from({ length: 10 }, (_, n) => <option key={n} value={n}>{n === 0 ? '0 (same as the document)' : `Level ${n}`}</option>)}
                </select>
                <small className="muted">Above 0, only roles with a rule at this level can see or change the field.</small>
                <Err message={errors[`${path}.permLevel`]} />
              </label>}

              {isTable && <label className="check-line">
                <input type="checkbox" checked={field.multiSelect ?? false} disabled={disabled} onChange={(e) => onChange({ multiSelect: e.target.checked })} />
                Show as a list of chips to pick from (the child table needs exactly one Link field)
                <Err message={errors[`${path}.multiSelect`]} />
              </label>}

              {submittable && !inChildTable && <label className="check-line">
                <input type="checkbox" checked={field.allowOnSubmit ?? false} disabled={disabled} onChange={(e) => onChange({ allowOnSubmit: e.target.checked })} />
                Can still be changed after the document is submitted
              </label>}

              <label className="field">
                <span>Help text</span>
                <input value={field.description ?? ''} disabled={disabled} onChange={(e) => onChange({ description: e.target.value })} />
                <Err message={errors[`${path}.description`]} />
              </label>
            </>
          )}
          {!layout && (
            <details className="field full logic" open={Boolean(field.dependsOn || field.mandatoryDependsOn || field.readOnlyDependsOn || field.formula || field.fetchFrom || field.default?.startsWith('='))}>
              <summary>Logic <span className="muted">(conditions, calculations, fetching)</span></summary>
              <div className="logic-body">
                <ExpressionInput label="Show only when" value={field.dependsOn ?? ''} names={fieldNames} allowParent={inChildTable}
                  disabled={disabled} placeholder="status == 'Closed'" serverError={errors[`${path}.dependsOn`]}
                  hint="Leave empty to always show the field. A field that is not shown is never required."
                  onChange={(value) => onChange({ dependsOn: value })} />

                {!isTable && (
                  <>
                    <ExpressionInput label="Required when" value={field.mandatoryDependsOn ?? ''} names={fieldNames} allowParent={inChildTable}
                      disabled={disabled} placeholder="amount > 1000" serverError={errors[`${path}.mandatoryDependsOn`]} help={false}
                      onChange={(value) => onChange({ mandatoryDependsOn: value })} />
                    <ExpressionInput label="Read-only when" value={field.readOnlyDependsOn ?? ''} names={fieldNames} allowParent={inChildTable}
                      disabled={disabled} placeholder="status == 'Closed'" serverError={errors[`${path}.readOnlyDependsOn`]} help={false}
                      onChange={(value) => onChange({ readOnlyDependsOn: value })} />
                    <ExpressionInput label="Calculate as (formula)" value={field.formula ?? ''} names={fieldNames.filter((n) => n !== field.name)}
                      allowParent={inChildTable} disabled={disabled || Boolean(field.fetchFrom)}
                      placeholder="qty * rate" serverError={errors[`${path}.formula`]}
                      hint="The field is filled in automatically, recalculated on every save, and cannot be edited."
                      onChange={(value) => onChange({ formula: value })} />
                    <FetchFromEditor field={field} linkFields={linkFields} disabled={disabled || Boolean(field.formula)}
                      error={errors[`${path}.fetchFrom`] ?? errors[`${path}.fetchIfEmpty`]} onChange={onChange} />
                    {field.default?.startsWith('=') && (
                      <small className={localExpressionError(field.default.slice(1), fieldNames, inChildTable) ? 'error-text' : 'muted'}>
                        {localExpressionError(field.default.slice(1), fieldNames, inChildTable) ?? 'The default is an expression, worked out when a document is created.'}
                      </small>
                    )}
                  </>
                )}
              </div>
            </details>
          )}
          <Err message={errors[`${path}.type`]} />
        </div>
      )}
    </li>
  )
}

export function PermissionsEditor({ permissions, roles, disabled, errors, onChange, errorPrefix = 'permissions', submittable = false }: {
  permissions: DocPerm[]
  roles: string[]
  disabled: boolean
  errors: Record<string, string>
  onChange: (next: DocPerm[]) => void
  /** The prefix of this table's error paths: the designer reports `permissions[0]`, the manager `rules[0]`. */
  errorPrefix?: string
  /** Submittable DocTypes also have Submit and Cancel rights. */
  submittable?: boolean
}) {
  const flags = (submittable
    ? ['read', 'write', 'create', 'delete', 'submit', 'cancel', 'export', 'report', 'import', 'share', 'print', 'email', 'ifOwner']
    : ['read', 'write', 'create', 'delete', 'export', 'report', 'import', 'share', 'print', 'email', 'ifOwner']) as ('read' | 'write' | 'create' | 'delete' | 'submit' | 'cancel' | 'export' | 'report' | 'import' | 'share' | 'print' | 'email' | 'ifOwner')[]
  const labels = { read: 'Read', write: 'Write', create: 'Create', delete: 'Delete', submit: 'Submit', cancel: 'Cancel', export: 'Export', report: 'Report', import: 'Import', share: 'Share', print: 'Print', email: 'Email', ifOwner: 'Own only' } as const

  const update = (index: number, patch: Partial<DocPerm>) =>
    onChange(permissions.map((p, i) => (i === index ? { ...p, ...patch } : p)))

  return (
    <div>
      <datalist id="role-list">{roles.map((r) => <option key={r} value={r} />)}</datalist>
      <div className="table-wrap">
        <table className="perm-table">
          <thead><tr><th>Role</th><th>Level</th>{flags.map((f) => <th key={f}>{labels[f]}</th>)}<th /></tr></thead>
          <tbody>
            {permissions.map((p, i) => (
              <tr key={i}>
                <td>
                  <input list="role-list" aria-label="Role" value={p.role} disabled={disabled}
                    onChange={(e) => update(i, { role: e.target.value })} />
                  {errors[`${errorPrefix}[${i}].role`] && <small className="error-text" role="alert">{errors[`${errorPrefix}[${i}].role`]}</small>}
                </td>
                <td>
                  <select aria-label={`Level for ${p.role || 'role'}`} value={p.permLevel ?? 0} disabled={disabled}
                    onChange={(e) => { const level = Number(e.target.value); update(i, level > 0 ? { permLevel: level, create: false, delete: false, submit: false, cancel: false, export: false, report: false, import: false, share: false, print: false, email: false } : { permLevel: level }) }}>
                    {Array.from({ length: 10 }, (_, n) => <option key={n} value={n}>{n}</option>)}
                  </select>
                  {errors[`${errorPrefix}[${i}].permLevel`] && <small className="error-text" role="alert">{errors[`${errorPrefix}[${i}].permLevel`]}</small>}
                </td>
                {flags.map((f) => (
                  <td key={f}>
                    <input type="checkbox" aria-label={`${labels[f]} for ${p.role || 'role'}`} checked={p[f] ?? (f === 'import' || f === 'share' || f === 'print' || f === 'email')} disabled={disabled || ((p.permLevel ?? 0) > 0 && (f === 'create' || f === 'delete' || f === 'submit' || f === 'cancel' || f === 'export' || f === 'report' || f === 'import' || f === 'share' || f === 'print' || f === 'email'))}
                      onChange={(e) => update(i, { [f]: e.target.checked })} />
                  </td>
                ))}
                <td><button type="button" className="btn small icon danger" aria-label="Remove rule" disabled={disabled}
                  onClick={() => onChange(permissions.filter((_, n) => n !== i))}>✕</button></td>
              </tr>
            ))}
            {permissions.length === 0 && <tr><td colSpan={flags.length + 3} className="empty">No rules: only Administrators can use this DocType.</td></tr>}
          </tbody>
        </table>
      </div>
      <button type="button" className="btn small btn-gap" disabled={disabled}
        onClick={() => onChange([...permissions, { role: '', read: true, write: false, create: false, delete: false, ifOwner: false, permLevel: 0, submit: false, cancel: false, export: false, report: false, import: true, share: true, print: true, email: true }])}>
        + Add rule
      </button>
    </div>
  )
}
