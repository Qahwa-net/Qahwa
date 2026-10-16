export interface RenamePair {
  from: string
  to: string
}

/**
 * The new names for the chosen documents: the text to find is replaced by the new text in each name (plain text, first match only
 * unless "all"), names that do not change are left out, and an empty result or a clash between new names is reported so the person
 * sees it before anything is sent. The server checks everything again.
 */
export function renamePairs(names: string[], find: string, replaceWith: string, all = true): { pairs: RenamePair[]; problem: string | null } {
  if (find === '') return { pairs: [], problem: null }
  const pairs: RenamePair[] = []
  for (const name of names) {
    if (!name.includes(find)) continue
    const to = all ? name.split(find).join(replaceWith) : name.replace(find, () => replaceWith)
    if (to.trim() === '') return { pairs: [], problem: `"${name}" would end up with no name.` }
    if (to.length > 140) return { pairs: [], problem: `The new name of "${name}" is too long.` }
    if (to !== name) pairs.push({ from: name, to })
  }

  const seen = new Set<string>()
  for (const pair of pairs) {
    if (seen.has(pair.to)) return { pairs: [], problem: `Two documents would both be named "${pair.to}".` }
    seen.add(pair.to)
  }

  return { pairs, problem: null }
}
