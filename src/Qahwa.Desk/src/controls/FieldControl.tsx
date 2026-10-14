import { useId, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client.ts'
import type { DocField } from '../api/types.ts'
import { isEditable, labelOf, selectOptions } from '../lib/fields.ts'

export type InputValue = string | boolean

interface Props {
  field: DocField
  value: InputValue
  onChange: (value: InputValue) => void
  error?: string
  readOnly?: boolean
  /** Designer preview: hidden fields are shown (dimmed) instead of omitted. */
  preview?: boolean
}

/** Suggests existing documents of the linked DocType as the user types. */
function LinkInput({ field, value, onChange, readOnly, id, invalid }: Props & { id: string; invalid: boolean }) {
  const listId = useId()
  const [search, setSearch] = useState('')
  const target = field.options ?? ''
  const suggestions = useQuery({
    queryKey: ['link', target, search],
    enabled: Boolean(target) && !readOnly && field.type === 'Link',
    queryFn: () =>
      api.list(target, {
        fields: ['name'],
        filters: search ? [{ field: 'name', op: 'like', value: search }] : [],
        orderBy: 'modified',
        descending: true,
        limit: 10,
        offset: 0,
      }),
    // A user who cannot read the target DocType simply gets no suggestions.
    retry: false,
  })

  return (
    <>
      <input id={id} list={listId} type="text" value={String(value)} readOnly={readOnly} aria-invalid={invalid}
        autoComplete="off"
        onChange={(e) => { onChange(e.target.value); setSearch(e.target.value) }} />
      <datalist id={listId}>
        {suggestions.data?.data.map((doc) => <option key={doc.name} value={doc.name} />)}
      </datalist>
    </>
  )
}

export function FieldControl(props: Props) {
  const { field, value, onChange, error, preview } = props
  const readOnly = Boolean(props.readOnly || field.readOnly)
  const id = useId()
  const label = labelOf(field)

  if (field.hidden && !preview) return null

  if (field.type === 'SectionBreak' || field.type === 'TabBreak') {
    return <h2 className="section-title full">{field.label ?? ''}</h2>
  }
  if (field.type === 'ColumnBreak') return null

  if (!isEditable(field.type)) {
    return (
      <div className="field">
        <span>{label}</span>
        <small className="muted">{field.type} fields are not supported in the Desk yet.</small>
      </div>
    )
  }

  const invalid = Boolean(error)
  const common = { id, 'aria-invalid': invalid, 'aria-describedby': invalid ? `${id}-error` : undefined, readOnly }
  const text = String(value)
  let control: React.ReactNode

  switch (field.type) {
    case 'Check':
      control = (
        <input id={id} type="checkbox" checked={value === true} disabled={readOnly}
          onChange={(e) => onChange(e.target.checked)} />
      )
      break
    case 'Select':
      control = (
        <select id={id} value={text} disabled={readOnly} aria-invalid={invalid} onChange={(e) => onChange(e.target.value)}>
          <option value="">{field.required ? 'Select…' : ''}</option>
          {selectOptions(field).map((o) => <option key={o} value={o}>{o}</option>)}
        </select>
      )
      break
    case 'Link':
    case 'DynamicLink':
      control = <LinkInput {...props} readOnly={readOnly} id={id} invalid={invalid} />
      break
    case 'Text':
    case 'LongText':
    case 'Markdown':
    case 'Code':
    case 'Json':
      control = (
        <textarea {...common} rows={field.type === 'Text' ? 3 : 6} value={text}
          className={field.type === 'Code' || field.type === 'Json' ? 'mono' : undefined}
          onChange={(e) => onChange(e.target.value)} />
      )
      break
    default: {
      const type = {
        Int: 'number', Float: 'number', Decimal: 'number', Currency: 'number', Percent: 'number',
        Date: 'date', Datetime: 'datetime-local', Time: 'time', Email: 'email', Url: 'url', Phone: 'tel',
      }[field.type as string] ?? 'text'
      control = (
        <input {...common} type={type} value={text} step={field.type === 'Int' ? 1 : type === 'number' ? 'any' : undefined}
          onChange={(e) => onChange(e.target.value)} />
      )
    }
  }

  const isCheck = field.type === 'Check'
  const wide = ['Text', 'LongText', 'Markdown', 'Code', 'Json'].includes(field.type)

  return (
    <div className={`field${isCheck ? ' check' : ''}${wide ? ' full' : ''}${field.hidden ? ' dimmed' : ''}`}>
      <label htmlFor={id}>
        {label}{field.required && <span className="req" aria-label="required"> *</span>}
        {field.hidden && <span className="badge"> hidden</span>}
      </label>
      {control}
      {field.description && <small className="muted">{field.description}</small>}
      {invalid && <small id={`${id}-error`} className="error-text" role="alert">{error}</small>}
    </div>
  )
}
