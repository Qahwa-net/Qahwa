import { useContext, useId, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { QuickCreate } from './QuickCreate.tsx'
import { InQuickCreate } from './QuickCreateContext.ts'
import { SearchSelect } from './SearchSelect.tsx'
import { api } from '../api/client.ts'
import type { DocField } from '../api/types.ts'
import { useT } from '../i18n.tsx'
import { isEditable, labelOf, selectOptions } from '../lib/fields.ts'
import { translateValue } from '../lib/i18n.ts'
import { LinkPreview } from './LinkPreview.tsx'
import type { FieldState } from '../lib/logic.ts'
import { LinkSelect } from './LinkSelect.tsx'
import { AttachInput, ColorInput, DurationInput, GeolocationInput, IconInput, RatingInput, SignatureInput } from './SpecialInputs.tsx'
import { PhoneInput } from './PhoneInput.tsx'
import { RichTextInput } from './RichTextInput.tsx'
import { sanitizeHtml } from '../lib/richText.ts'

export type InputValue = string | boolean

interface InputProps {
  field: DocField
  value: InputValue
  onChange: (value: InputValue) => void
  readOnly?: boolean
  invalid?: boolean
  id?: string
  /** Used for cells in a table grid, where there is no visible label. */
  ariaLabel?: string
  /** The other values of the document (or table row): a Dynamic Link reads its DocType from one of them. */
  siblings?: Record<string, InputValue>
}

/** Choose an existing document of the linked DocType by typing part of its name; shows friendly names, not IDs. */
function LinkInput({ field, value, onChange, readOnly, id, invalid, ariaLabel, siblings }: InputProps) {
  // A Dynamic Link names the field that holds its DocType; a Link names the DocType itself.
  const target = field.type === 'DynamicLink' ? String(siblings?.[field.options ?? ''] ?? '') : (field.options ?? '')
  const inQuickCreate = useContext(InQuickCreate)
  const [creating, setCreating] = useState<string | null>(null)
  const targetMeta = useQuery({ queryKey: ['meta', target], queryFn: () => api.meta(target), enabled: Boolean(target) && !readOnly && !inQuickCreate, retry: false })
  if (!target) {
    return <input id={id} type="text" value="" disabled placeholder="Choose the DocType first" aria-invalid={invalid} aria-label={ariaLabel} readOnly />
  }

  return (
    <><LinkSelect
      id={id} ariaLabel={ariaLabel} invalid={invalid} readOnly={readOnly} value={String(value)}
      key={target}
      cacheKey={['link', target]}
      search={(text) => api.linkSearch(target, text)}
      resolve={async (name) => (await api.linkTitles(target, [name]))[name]}
      onChange={(next) => onChange(next)}
      onCreate={!readOnly && !inQuickCreate && targetMeta.data?.permissions.create && !targetMeta.data.data.isSingle ? text => setCreating(text) : undefined}
    />
    {Boolean(value) && <LinkPreview doctype={target} name={String(value)} />}
    {creating !== null && <QuickCreate doctype={target} initialText={creating} onClose={() => setCreating(null)} onCreated={doc => { onChange(doc.name); setCreating(null) }} />}</>
  )
}

/** The bare input for a field (no label, no help text). Shared by form fields and table cells. */
export function FieldInput(props: InputProps) {
  const { field, value, onChange, readOnly, invalid, id, ariaLabel } = props
  const text = String(value)
  const common = { id, 'aria-invalid': invalid, 'aria-label': ariaLabel, readOnly }

  switch (field.type) {
    case 'Check':
      return <input id={id} aria-label={ariaLabel} type="checkbox" checked={value === true} disabled={readOnly}
        onChange={(e) => onChange(e.target.checked)} />
    case 'Select':
      return (
        <SearchSelect id={id} label={ariaLabel} value={text} readOnly={readOnly} invalid={invalid} options={selectOptions(field).map(o => ({ value: o, label: field.translatable ? translateValue(o) : o }))} onChange={onChange} />
      )
    case 'Link':
    case 'DynamicLink':
      return <LinkInput {...props} />
    case 'Attach': case 'AttachImage': return <AttachInput id={id} ariaLabel={ariaLabel} value={value} readOnly={readOnly} invalid={invalid} image={field.type === 'AttachImage'} onChange={onChange} />
    case 'Icon': return <IconInput id={id} ariaLabel={ariaLabel} value={value} readOnly={readOnly} invalid={invalid} onChange={onChange} />
    case 'Rating': return <RatingInput id={id} ariaLabel={ariaLabel} value={value} readOnly={readOnly} invalid={invalid} onChange={onChange} />
    case 'Duration': return <DurationInput id={id} ariaLabel={ariaLabel} value={value} readOnly={readOnly} invalid={invalid} onChange={onChange} />
    case 'Color': return <ColorInput id={id} ariaLabel={ariaLabel} value={value} readOnly={readOnly} invalid={invalid} onChange={onChange} />
    case 'Geolocation': return <GeolocationInput id={id} ariaLabel={ariaLabel} value={value} readOnly={readOnly} invalid={invalid} onChange={onChange} />
    case 'Signature': return <SignatureInput id={id} ariaLabel={ariaLabel} value={value} readOnly={readOnly} invalid={invalid} onChange={onChange} />
    case 'Password':
      return <input {...common} type="password" maxLength={1000} autoComplete="new-password" value={text} onChange={(e) => onChange(e.target.value)} />
    case 'Phone': return <PhoneInput id={id} ariaLabel={ariaLabel} value={text} readOnly={readOnly} invalid={invalid} onChange={onChange} />
    case 'TextEditor': return <RichTextInput id={id ?? ""} ariaLabel={ariaLabel} value={text} readOnly={readOnly} invalid={invalid} onChange={onChange} />
    case 'SmallText':
      return <textarea {...common} rows={2} maxLength={5000} value={text} onChange={(e) => onChange(e.target.value)} />
    case 'Autocomplete': {
      const listId = `${id ?? field.name}-suggestions`
      return (
        <>
          <input {...common} maxLength={140} list={listId} autoComplete="off" value={text} onChange={(e) => onChange(e.target.value)} />
          <datalist id={listId}>{selectOptions(field).map((o) => <option key={o} value={o} />)}</datalist>
        </>
      )
    }
    case 'Barcode':
      return <input {...common} className="mono" maxLength={140} inputMode="text" autoComplete="off" value={text} onChange={(e) => onChange(e.target.value)} />
    case 'Text':
    case 'LongText':
    case 'Markdown':
    case 'Code':
    case 'Json':
      return (
        <textarea {...common} rows={field.type === 'Text' ? 3 : 6} value={text}
          className={field.type === 'Code' || field.type === 'Json' ? 'mono' : undefined}
          onChange={(e) => onChange(e.target.value)} />
      )
    default: {
      const type = {
        Int: 'number', Float: 'number', Decimal: 'number', Currency: 'number', Percent: 'number',
        Date: 'date', Datetime: 'datetime-local', Time: 'time', Email: 'email', Url: 'url', Phone: 'tel',
      }[field.type as string] ?? 'text'
      return (
        <input {...common} type={type} value={text} step={field.type === 'Int' ? 1 : type === 'number' ? 'any' : undefined}
          onChange={(e) => onChange(e.target.value)} />
      )
    }
  }
}

interface Props {
  field: DocField
  value: InputValue
  onChange: (value: InputValue) => void
  error?: string
  readOnly?: boolean
  /** Designer preview: hidden fields are shown (dimmed) instead of omitted. */
  preview?: boolean
  /** What the field logic says right now: shown, required and read-only. Without it the field is static. */
  state?: FieldState
  /** The other values of the document or row (for Dynamic Links). */
  siblings?: Record<string, InputValue>
}

export function FieldControl(props: Props) {
  const { field, value, onChange, error, preview, state } = props
  const readOnly = Boolean(props.readOnly || field.readOnly || state?.readOnly)
  const id = useId()
  const t = useT()
  const label = t(labelOf(field))

  if (field.hidden && !preview) return null
  // The link to the cancelled document an amendment replaces is only worth showing when there is one (ADR-103).
  if (field.name === 'amended_from' && !preview && (value === '' || value === null || value === undefined)) return null
  if (state && !state.active && !preview) return null

  if (field.type === 'SectionBreak' || field.type === 'TabBreak') {
    return <h2 className="section-title full">{field.label ?? ''}</h2>
  }
  if (field.type === 'ColumnBreak') return null
  if (field.type === 'Heading') return <p className="form-heading full"><strong>{t(labelOf(field))}</strong></p>
  if (field.type === 'Html') return <div className="form-html full" dangerouslySetInnerHTML={{ __html: sanitizeHtml(field.options ?? '') }} />

  if (!isEditable(field.type)) {
    return (
      <div className="field">
        <span>{label}</span>
        <small className="muted">{field.type} fields are not supported in the Desk yet.</small>
      </div>
    )
  }

  const invalid = Boolean(error)
  const required = state ? state.required : field.required
  const conditional = Boolean(state && !state.active)
  const isCheck = field.type === 'Check'
  const wide = ['Text', 'SmallText', 'TextEditor', 'LongText', 'Markdown', 'Code', 'Json'].includes(field.type)

  return (
    <div className={`field${isCheck ? ' check' : ''}${wide ? ' full' : ''}${field.hidden || conditional ? ' dimmed' : ''}`}>
      <label htmlFor={id} style={field.bold ? { fontWeight: 700 } : undefined}>
        {label}{required && <span className="req" aria-label="required"> *</span>}
        {field.hidden && <span className="badge"> hidden</span>}
        {conditional && <span className="badge" title={field.dependsOn ?? undefined}> only when its condition holds</span>}
      </label>
      <FieldInput field={field} value={value} onChange={onChange} readOnly={readOnly} invalid={invalid} id={id} siblings={props.siblings} />
      {field.description && <small className="muted">{field.description}</small>}
      {invalid && <small id={`${id}-error`} className="error-text" role="alert">{error}</small>}
    </div>
  )
}
