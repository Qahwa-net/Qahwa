import type { DocTypeMeta, IndicatorColor } from '../api/types.ts'

const DOCSTATUS: { text: string; color: IndicatorColor }[] = [
  { text: 'Draft', color: 'orange' },
  { text: 'Submitted', color: 'green' },
  { text: 'Cancelled', color: 'red' },
]

/** A small coloured label: the colour is a class, the text is plain text. */
export function StatusPill({ text, color }: { text: string; color: IndicatorColor }) {
  return <span className={`pill pill-${color}`}>{text}</span>
}

/** The pills that describe a row: the DocType's status field in its colour, and Draft/Submitted/Cancelled for submittable DocTypes. */
export function RowStatus({ meta, doc }: { meta: DocTypeMeta; doc: Record<string, unknown> }) {
  const status = meta.statusField ? doc[meta.statusField] : null
  const value = status === null || status === undefined || status === '' ? null : String(status)
  const color = meta.indicators?.find((i) => i.value === value)?.color ?? 'gray'
  const docstatus = meta.isSubmittable ? DOCSTATUS[Number(doc.docstatus ?? 0)] : undefined

  if (!value && !docstatus) return null
  return (
    <span className="row-status">
      {value && <StatusPill text={value} color={color} />}
      {docstatus && <StatusPill text={docstatus.text} color={docstatus.color} />}
    </span>
  )
}
