/** "5 minutes", "1 hour": how often the scheduler wakes up. */
export function paceText(seconds: number): string {
  if (seconds % 3600 === 0) return seconds === 3600 ? 'hour' : `${seconds / 3600} hours`
  if (seconds % 60 === 0) return seconds === 60 ? 'minute' : `${seconds / 60} minutes`
  return `${seconds} seconds`
}

/** How long ago, in the largest sensible unit ("just now", "5 minutes ago", "3 hours ago", "2 days ago"). */
export function agoText(iso: string, now = Date.now()): string {
  const seconds = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000))
  if (seconds < 60) return 'just now'
  const unit = (n: number, name: string) => `${n} ${name}${n === 1 ? '' : 's'} ago`
  if (seconds < 3600) return unit(Math.floor(seconds / 60), 'minute')
  if (seconds < 86400) return unit(Math.floor(seconds / 3600), 'hour')
  return unit(Math.floor(seconds / 86400), 'day')
}
