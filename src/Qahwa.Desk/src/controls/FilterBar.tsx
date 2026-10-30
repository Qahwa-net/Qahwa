import { useState } from 'react'
import type { DocField } from '../api/types.ts'
import { kindOf, labelOf, operatorsFor, selectOptions } from '../lib/fields.ts'
import { describeFilter, TIMESPANS, type Filter, type FilterOp } from '../lib/filters.ts'
import { Select } from './Select.tsx'

interface Props {
  fields: DocField[]
  filters: Filter[]
  onChange: (filters: Filter[]) => void
}

/** Builds one filter value from whatever the chosen operator and field type needs. */
function buildValue(field: DocField, op: FilterOp, raw: string): Filter['value'] | null {
  if (op === 'is') return raw === 'not set' ? 'not set' : 'set'
  if (raw === '') return null
  if (op === 'timespan') return (TIMESPANS as readonly string[]).includes(raw) ? raw : null
  if (op === 'between') {
    // Two ends kept in one text box as "from|to" (the form below shows two boxes).
    const [from, to] = raw.split('|')
    if (!from || !to) return null
    const ends = [from, to].map((e) => (kindOf(field.type) === 'number' ? Number(e) : e))
    return ends.some((e) => typeof e === 'number' && !Number.isFinite(e)) ? null : [ends[0]!, ends[1]!]
  }

  switch (kindOf(field.type)) {
    case 'number': {
      const n = Number(raw)
      return Number.isFinite(n) ? n : null
    }
    case 'check': return raw === 'true'
    case 'datetime': {
      const d = new Date(raw)
      return Number.isNaN(d.getTime()) ? null : d.toISOString()
    }
    default: return raw
  }
}

function ValueInput({ field, op, value, onChange }: {
  field: DocField; op: FilterOp; value: string; onChange: (v: string) => void
}) {
  const label = 'Filter value'
  if (op === 'timespan') {
    return (
      <Select aria-label={label} value={value || 'today'} onChange={(e) => onChange(e.target.value)}>
        {TIMESPANS.map((t) => <option key={t} value={t}>{t}</option>)}
      </Select>
    )
  }

  if (op === 'between') {
    const [from = '', to = ''] = value.split('|')
    const type = kindOf(field.type) === 'number' ? 'number' : kindOf(field.type) === 'datetime' ? 'date' : 'date'
    return (
      <>
        <input aria-label="From" type={type} step="any" value={from} onChange={(e) => onChange(`${e.target.value}|${to}`)} />
        <input aria-label="To" type={type} step="any" value={to} onChange={(e) => onChange(`${from}|${e.target.value}`)} />
      </>
    )
  }

  if (op === 'is') {
    return (
      <Select aria-label={label} value={value || 'set'} onChange={(e) => onChange(e.target.value)}>
        <option value="set">set</option>
        <option value="not set">not set</option>
      </Select>
    )
  }

  switch (kindOf(field.type)) {
    case 'check':
      return (
        <Select aria-label={label} value={value || 'true'} onChange={(e) => onChange(e.target.value)}>
          <option value="true">Yes</option>
          <option value="false">No</option>
        </Select>
      )
    case 'select':
      return (
        <Select aria-label={label} value={value} onChange={(e) => onChange(e.target.value)}>
          <option value="" />
          {selectOptions(field).map((o) => <option key={o} value={o}>{o}</option>)}
        </Select>
      )
    case 'number':
      return <input aria-label={label} type="number" step="any" value={value} onChange={(e) => onChange(e.target.value)} />
    case 'date':
      return <input aria-label={label} type="date" value={value} onChange={(e) => onChange(e.target.value)} />
    case 'datetime':
      return <input aria-label={label} type="datetime-local" value={value} onChange={(e) => onChange(e.target.value)} />
    case 'time':
      return <input aria-label={label} type="time" value={value} onChange={(e) => onChange(e.target.value)} />
    default:
      return <input aria-label={label} type="text" value={value} onChange={(e) => onChange(e.target.value)} />
  }
}

export function FilterBar({ fields, filters, onChange }: Props) {
  const [adding, setAdding] = useState(false)
  const [fieldName, setFieldName] = useState(fields[0]?.name ?? '')
  const [op, setOp] = useState<FilterOp>('like')
  const [raw, setRaw] = useState('')

  const field = fields.find((f) => f.name === fieldName) ?? fields[0]
  const labels = new Map(fields.map((f) => [f.name, labelOf(f)]))

  function chooseField(name: string) {
    const next = fields.find((f) => f.name === name)
    setFieldName(name)
    setOp(next ? (operatorsFor(next.type)[0]?.op ?? '=') : '=')
    setRaw('')
  }

  function add() {
    if (!field) return
    const value = buildValue(field, op, raw)
    if (value === null) return
    onChange([...filters, { field: field.name, op, value }])
    setRaw('')
    setAdding(false)
  }

  return (
    <div className="filter-bar">
      <div className="chips">
        {filters.map((filter, index) => (
          <span className="chip" key={`${filter.field}-${index}`}>
            {describeFilter(filter, labels.get(filter.field) ?? filter.field)}
            <button type="button" aria-label={`Remove filter ${labels.get(filter.field) ?? filter.field}`}
              onClick={() => onChange(filters.filter((_, i) => i !== index))}>×</button>
          </span>
        ))}
        <button type="button" className="btn small" aria-expanded={adding}
          onClick={() => { setAdding(!adding); if (!adding && field) chooseField(field.name) }}>
          + Filter
        </button>
        {filters.length > 0 && <button type="button" className="btn small ghost" onClick={() => onChange([])}>Clear all</button>}
      </div>

      {adding && field && (
        <form className="filter-form" onSubmit={(e) => { e.preventDefault(); add() }}>
          <Select aria-label="Filter field" value={field.name} onChange={(e) => chooseField(e.target.value)}>
            {fields.map((f) => <option key={f.name} value={f.name}>{labelOf(f)}</option>)}
          </Select>
          <Select aria-label="Filter operator" value={op} onChange={(e) => setOp(e.target.value as FilterOp)}>
            {operatorsFor(field.type).map((o) => <option key={o.op} value={o.op}>{o.label}</option>)}
          </Select>
          <ValueInput field={field} op={op} value={raw} onChange={setRaw} />
          <button className="btn primary small" type="submit">Apply</button>
        </form>
      )}
    </div>
  )
}
