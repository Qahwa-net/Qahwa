/** "812 B", "4.5 KB", "12 MB". */
export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  const units = ['KB', 'MB', 'GB']
  let value = bytes / 1024
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  const text = value >= 10 || Number.isInteger(value) ? String(Math.round(value)) : value.toFixed(1)
  return `${text} ${units[unit]}`
}

export const MAX_FILE_BYTES = 25 * 1024 * 1024

/** Why a file should not even be sent, or null. The server checks again. */
export function fileProblem(file: { name: string; size: number }, max = MAX_FILE_BYTES): string | null {
  if (file.size === 0) return `${file.name} is empty.`
  if (file.size > max) return `${file.name} is larger than ${Math.round(max / (1024 * 1024))} MB.`
  return null
}
