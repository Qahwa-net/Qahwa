/** Host names typed one per line or separated by commas/spaces: trimmed, lower-cased, no duplicates, no blanks. */
export function parseHosts(text: string): string[] {
  const seen = new Set<string>()
  for (const part of text.split(/[\s,;]+/)) {
    const host = part.trim().toLowerCase()
    if (host) seen.add(host)
  }
  return [...seen]
}

/** The rule the server applies to a site name (it checks again). */
export const validSiteName = (name: string): boolean => /^[a-z0-9][a-z0-9-]{0,39}$/.test(name)
