export interface DisplaySettings {
  timeZone: string
  dateFormat: string
  firstDayOfWeek: string
  defaultLanguage: string
  /** How numbers are grouped and where the decimal mark goes: "1,234.56", "1.234,56" or "1 234,56". */
  numberFormat?: string
}

export const DEFAULT_DISPLAY: DisplaySettings = { timeZone: 'UTC', dateFormat: 'yyyy-mm-dd', firstDayOfWeek: 'Monday', defaultLanguage: 'en', numberFormat: '1,234.56' }

let current: DisplaySettings = DEFAULT_DISPLAY

/** Called once the settings are known, so every date shown after that follows them. */
export function setDisplaySettings(settings: DisplaySettings): void {
  current = settings
}

export const getDisplaySettings = (): DisplaySettings => current

/** A number written with the chosen separators (at most nine decimals). Anything that is not a finite number is shown as it is. */
export function formatNumber(value: unknown, settings: DisplaySettings = current): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) return String(value)
  const [whole, fraction] = Math.abs(value).toLocaleString('en-US', { maximumFractionDigits: 9, useGrouping: false }).split('.')
  const [group, mark] = settings.numberFormat === '1.234,56' ? ['.', ','] : settings.numberFormat === '1 234,56' ? [' ', ','] : [',', '.']
  const grouped = whole!.replace(/\B(?=(\d{3})+(?!\d))/g, group)
  return `${value < 0 ? '-' : ''}${grouped}${fraction ? mark + fraction : ''}`
}

/** 0 = Sunday, 1 = Monday, 6 = Saturday: the weekday a calendar week starts on. */
export const weekStartIndex = (settings: DisplaySettings = current): number => (settings.firstDayOfWeek === 'Sunday' ? 0 : settings.firstDayOfWeek === 'Saturday' ? 6 : 1)

function order(settings: DisplaySettings, y: string, m: string, d: string): string {
  switch (settings.dateFormat) {
    case 'dd/mm/yyyy': return `${d}/${m}/${y}`
    case 'mm/dd/yyyy': return `${m}/${d}/${y}`
    default: return `${y}-${m}-${d}`
  }
}

/** A Date value ("2026-03-04" or a longer ISO text) in the chosen format. Anything that is not a date is shown as it is. */
export function formatDate(value: unknown, settings: DisplaySettings = current): string {
  const text = String(value ?? '')
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(text)
  return match ? order(settings, match[1]!, match[2]!, match[3]!) : text
}

/** A moment in the chosen time zone and date format, for example "04/03/2026 14:30". */
export function formatDateTime(value: unknown, settings: DisplaySettings = current): string {
  const text = String(value ?? '')
  const date = new Date(text)
  if (Number.isNaN(date.getTime())) return text
  try {
    const parts = Object.fromEntries(new Intl.DateTimeFormat('en-GB', {
      timeZone: settings.timeZone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
    }).formatToParts(date).map((p) => [p.type, p.value]))
    return `${order(settings, parts.year!, parts.month!, parts.day!)} ${parts.hour}:${parts.minute}`
  } catch {
    return date.toLocaleString() // a time zone this browser does not know
  }
}
