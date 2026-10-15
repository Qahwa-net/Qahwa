export const MAX_IMPORT_BYTES = 5 * 1024 * 1024

/** Whether a file can be offered for import (a CSV or text file within the size limit), and if not why. */
export function checkImportFile(file: { name: string; size: number; type?: string }): string | null {
  if (file.size > MAX_IMPORT_BYTES) return `The file is larger than ${MAX_IMPORT_BYTES / (1024 * 1024)} MB.`
  if (!/\.(csv|txt)$/i.test(file.name) && !/(csv|text\/plain)/i.test(file.type ?? '')) return 'Choose a CSV file (a spreadsheet saved as CSV).'
  return null
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
