import { Expression, ExpressionError } from './expr.ts'

const STANDARD = ['name', 'owner', 'creation', 'modified', 'modified_by', 'docstatus', 'idx']

/** The first problem with an expression that can be found without the server, or null. */
export function localExpressionError(source: string, names: string[], allowParent: boolean): string | null {
  if (source.trim() === '') return null
  try {
    const { identifiers, usesParent } = Expression.parse(source).analysis
    const known = new Set([...names, ...STANDARD])
    const unknown = [...identifiers].find((id) => !known.has(id))
    if (unknown) return `Unknown field '${unknown}'.`
    if (usesParent && !allowParent) return "'parent' can only be used in a child table."
    return null
  } catch (error) {
    if (error instanceof ExpressionError) return `${error.message} (character ${error.position + 1})`
    throw error
  }
}
