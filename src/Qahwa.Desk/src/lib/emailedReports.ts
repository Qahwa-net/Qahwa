export interface EmailedReport {
  id: string
  report: string
  reportTitle: string | null
  recipients: string[]
  frequency: 'Daily' | 'Weekly' | 'Monthly'
  sendHour: number
  nextSend: string | null
  status: 'Active' | 'Paused'
  runAs: string
  maxRows: number
  subject: string | null
  lastSent: string | null
  sentCount: number
  lastError: string | null
}

export interface EmailedReportRequest {
  report?: string
  recipients?: string
  frequency?: string
  sendHour?: number
  runAs?: string
  maxRows?: number
  subject?: string
  status?: string
}

export const MAX_RECIPIENTS = 10

/** The addresses typed in the box (one per line, or separated by commas or semicolons), without repeats. */
export function parseRecipients(text: string): string[] {
  const seen = new Set<string>()
  const list: string[] = []
  for (const part of text.split(/[\n\r,;]+/)) {
    const address = part.trim()
    if (address && !seen.has(address.toLowerCase())) {
      seen.add(address.toLowerCase())
      list.push(address)
    }
  }
  return list
}

const ADDRESS = /^[^\s@<>"',;]+@[^\s@<>"',;]+\.[^\s@<>"',;]+$/

/** What is wrong with the form, in words (the server checks again), or null. */
export function problemWith(recipients: string, report: string, runAs: string, maxRows: number): string | null {
  if (!report) return 'Choose a report.'
  const list = parseRecipients(recipients)
  if (list.length === 0) return 'Give at least one email address.'
  if (list.length > MAX_RECIPIENTS) return `At most ${MAX_RECIPIENTS} addresses.`
  const bad = list.find((a) => !ADDRESS.test(a))
  if (bad) return `'${bad.slice(0, 60)}' is not an email address.`
  if (!runAs.trim()) return 'Say whose permissions the report runs with.'
  if (!Number.isInteger(maxRows) || maxRows < 1 || maxRows > 200) return 'Send between 1 and 200 rows.'
  return null
}

/** "Every day at 08:00 UTC", "Every week …", "Every month …". */
export function describeSchedule(frequency: string, sendHour: number): string {
  const when = `${String(sendHour).padStart(2, '0')}:00 UTC`
  const every = frequency === 'Weekly' ? 'Every week' : frequency === 'Monthly' ? 'Every month' : 'Every day'
  return `${every} at ${when}`
}
