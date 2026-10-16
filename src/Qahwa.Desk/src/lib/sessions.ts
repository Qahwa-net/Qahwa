/** A short description of a browser from its user-agent text, for people (not for decisions). Anything unknown is "A browser". */
export function describeAgent(agent: string | null | undefined): string {
  if (!agent) return 'A browser'
  const browser = /Edg\//.test(agent) ? 'Edge' : /OPR\//.test(agent) ? 'Opera' : /Firefox\//.test(agent) ? 'Firefox' : /Chrome\//.test(agent) ? 'Chrome' : /Safari\//.test(agent) ? 'Safari' : null
  const system = /Windows/.test(agent) ? 'Windows' : /Android/.test(agent) ? 'Android' : /iPhone|iPad|iOS/.test(agent) ? 'iOS' : /Mac OS X|Macintosh/.test(agent) ? 'macOS' : /Linux/.test(agent) ? 'Linux' : null
  if (browser && system) return `${browser} on ${system}`
  return browser ?? system ?? agent.slice(0, 40)
}

/** "just now", "5 minutes ago", "3 hours ago", "2 days ago". */
export function ago(iso: string, now: Date = new Date()): string {
  const seconds = Math.max(0, Math.round((now.getTime() - new Date(iso).getTime()) / 1000))
  if (seconds < 60) return 'just now'
  const unit = (n: number, name: string) => `${n} ${name}${n === 1 ? '' : 's'} ago`
  if (seconds < 3600) return unit(Math.floor(seconds / 60), 'minute')
  if (seconds < 86400) return unit(Math.floor(seconds / 3600), 'hour')
  return unit(Math.floor(seconds / 86400), 'day')
}
