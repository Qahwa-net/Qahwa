import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client.ts'
import type { DocField } from '../api/types.ts'
import type { InputValue } from '../controls/FieldControl.tsx'
import { hasColumn, labelOf } from '../lib/fields.ts'

const plain = (f: DocField) => hasColumn(f.type) && !['Password', 'Attach', 'AttachImage'].includes(f.type) && !f.formula

/** Metadata-backed reference next to the actual automation input. */
export function DocumentActionFields({ field, values, readOnly, onChange }: {
  field: string
  values: Record<string, InputValue>
  readOnly: boolean
  onChange: (value: string) => void
}) {
  const source = String(values.reference_doctype ?? '')
  const target = values.action === 'Create Document' ? String(values.target_doctype ?? '') : source
  const sourceQuery = useQuery({ queryKey: ['meta', source], queryFn: () => api.meta(source), enabled: !!source })
  const targetQuery = useQuery({ queryKey: ['meta', target], queryFn: () => api.meta(target), enabled: !!target })
  const from = sourceQuery.data?.data
  const to = targetQuery.data?.data
  const sourceFields = from?.fields.filter(f => hasColumn(f.type) && f.type !== 'Password') ?? []
  const targetFields = to?.fields.filter(plain) ?? []
  const example = (f: DocField) => {
    if (sourceFields.some(s => s.name === f.name && (s.type === f.type || s.options === f.options))) return `${f.name} = {{${f.name}}}`
    if (f.type === 'Link' && f.options === source) return `${f.name} = {{name}}`
    const literal = f.default && !f.default.startsWith('=') ? f.default
      : f.type === 'Select' ? f.options?.split('\n').filter(Boolean)[0]
      : ['Int', 'Float', 'Decimal', 'Currency', 'Percent', 'Check'].includes(f.type) ? '1'
      : f.type === 'Date' ? '2026-01-01' : 'your value'
    return `${f.name} = ${literal ?? 'your value'}`
  }
  const add = (line: string) => {
    const text = String(values[field] ?? '').trimEnd()
    onChange(field === 'mapping' ? `${text}${text ? '\n' : ''}${line}` : line)
  }
  if (field === 'event') return from && ['Submit', 'Cancel'].includes(String(values.event)) && !from.isSubmittable
    ? <p role="alert" className="error-text">{source} does not support submission. Use ValueChange with a workflow field, or New / Update.</p> : null
  if (field === 'watch_field' || field === 'target_field') {
    const fields = field === 'watch_field' ? sourceFields : sourceFields.filter(plain)
    return <details className="action-field-help"><summary>Available {field === 'watch_field' ? 'fields to watch' : 'fields to set'}</summary>
      <div className="action-field-chips">{fields.map(f => <button key={f.name} type="button" className="btn btn-sm" disabled={readOnly} onClick={() => onChange(f.name)}>{labelOf(f)} <code>{f.name}</code></button>)}</div>
    </details>
  }
  return <div className="action-field-help">
    <p>Use <code>{'{{field}}'}</code> for a value from the existing {source || 'source document'}. {field === 'mapping' && 'One target field = value per line. Click an example to append it; replace sample values before saving.'}</p>
    {(sourceQuery.isError || targetQuery.isError) && <p role="alert">Unable to load field definitions. Check your access to the selected DocTypes.</p>}
    {(sourceQuery.isPending && !!source || targetQuery.isPending && !!target) && <p>Loading fields…</p>}
    <details><summary>Existing document: {source || 'choose a DocType'}</summary>
      <div className="action-field-chips">{[...sourceFields.map(f => ({ name: f.name, label: labelOf(f) })), ...['name', 'doctype', 'user'].map(name => ({ name, label: name }))].map(f =>
        <button key={f.name} type="button" className="btn btn-sm" disabled={readOnly || field === 'mapping'} onClick={() => add(`{{${f.name}}}`)} title={f.label}><code>{`{{${f.name}}}`}</code></button>)}</div>
      {from?.fields.some(f => f.type === 'Table') && <p>Child tables cannot be copied by this action. Only plain field placeholders are supported.</p>}
    </details>
    {field === 'mapping' && <details open><summary>New document: {target || 'choose a DocType'}</summary>
      {targetFields.map(f => <div key={f.name} className="action-field-example"><span>{labelOf(f)}{f.required ? ' *' : ''} <small>({f.type})</small></span>
        <button type="button" className="btn btn-sm" disabled={readOnly} onClick={() => add(example(f))}><code>{example(f)}</code></button></div>)}
      {to?.fields.filter(f => f.required && f.type === 'Table').map(f => <p key={f.name} role="alert" className="error-text">{labelOf(f)} is a required child table. Create Document actions cannot populate child tables, so this target may fail its required-field or table validations.</p>)}
    </details>}
  </div>
}
