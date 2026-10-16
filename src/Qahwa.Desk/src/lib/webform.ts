import type { PublicFormField } from '../api/types.ts'

export type Answers = Record<string, string | boolean>

/** The JSON to send: numbers as numbers, yes/no as booleans, empty answers left out. Only fields the form asked for are included. */
export function toPayload(fields: PublicFormField[], answers: Answers): Record<string, string | number | boolean> {
  const payload: Record<string, string | number | boolean> = {}
  for (const field of fields) {
    const raw = answers[field.name]
    if (field.type === 'Check') {
      payload[field.name] = raw === true
      continue
    }
    const text = typeof raw === 'string' ? raw.trim() : ''
    if (text === '') continue
    payload[field.name] = ['Int', 'Float', 'Decimal', 'Currency', 'Percent', 'Rating'].includes(field.type) && Number.isFinite(Number(text)) ? Number(text) : text
  }

  return payload
}

/** What the browser's own input type should be for a field. */
export function inputType(type: string): string {
  switch (type) {
    case 'Int': case 'Float': case 'Decimal': case 'Currency': case 'Percent': case 'Rating': return 'number'
    case 'Color': return 'color'
    case 'Email': return 'email'
    case 'Phone': return 'tel'
    case 'Url': return 'url'
    case 'Date': return 'date'
    case 'Datetime': return 'datetime-local'
    case 'Time': return 'time'
    default: return 'text'
  }
}

export const isMultiline = (type: string): boolean => type === 'Text' || type === 'SmallText' || type === 'LongText'

/** Names of required fields that were left empty (the server checks too). */
export function missing(fields: PublicFormField[], answers: Answers): string[] {
  return fields.filter((f) => f.required && f.type !== 'Check' && (typeof answers[f.name] !== 'string' || (answers[f.name] as string).trim() === '')).map((f) => f.name)
}
