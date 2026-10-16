export const MAX_IMPORT_BYTES = 5 * 1024 * 1024
export const MAX_XLSX_BYTES = 10 * 1024 * 1024

/** What the server is sent: the text of a CSV file, or a spreadsheet as base64. */
export interface ImportSource {
  csv?: string
  xlsx?: string
}

export const isSpreadsheet = (file: { name: string; type?: string }): boolean =>
  /\.xlsx$/i.test(file.name) || /spreadsheetml\.sheet/i.test(file.type ?? '')

/** Whether a file can be offered for import (a CSV, text or Excel file within the size limit), and if not why. */
export function checkImportFile(file: { name: string; size: number; type?: string }): string | null {
  if (isSpreadsheet(file)) return file.size > MAX_XLSX_BYTES ? `The file is larger than ${MAX_XLSX_BYTES / (1024 * 1024)} MB.` : null
  if (file.size > MAX_IMPORT_BYTES) return `The file is larger than ${MAX_IMPORT_BYTES / (1024 * 1024)} MB.`
  if (!/\.(csv|txt)$/i.test(file.name) && !/(csv|text\/plain)/i.test(file.type ?? '')) return 'Choose an Excel (.xlsx) or CSV file.'
  return null
}

/** Base64 of some bytes, built in pieces so a large file does not overflow the call stack. */
export function toBase64(bytes: Uint8Array): string {
  let binary = ''
  for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000))
  return btoa(binary)
}

/** "3 created, 1 updated, 2 failed" with only the parts that apply. */
export function summarise(result: { created: number; updated: number; failed: number }): string {
  const parts = [
    result.created > 0 ? `${result.created} created` : null,
    result.updated > 0 ? `${result.updated} updated` : null,
    result.failed > 0 ? `${result.failed} failed` : null,
  ].filter(Boolean)
  return parts.length > 0 ? parts.join(', ') : 'Nothing was imported'
}
