import { describe, expect, it } from 'vitest'
import { localExpressionError } from './check.ts'

describe('localExpressionError', () => {
  const names = ['qty', 'rate', 'status']

  it('accepts valid expressions over known fields, the standard fields and nothing at all', () => {
    expect(localExpressionError('qty * rate', names, false)).toBeNull()
    expect(localExpressionError("owner == user() && status == 'Open'", names, false)).toBeNull()
    expect(localExpressionError('   ', names, false)).toBeNull()
  })

  it('points at syntax errors with the character position', () => {
    expect(localExpressionError('qty *', names, false)).toMatch(/end.*\(character 6\)/)
    expect(localExpressionError('nosuch(1)', names, false)).toContain('Unknown function')
  })

  it('refuses unknown fields and a misplaced parent', () => {
    expect(localExpressionError('qty * price', names, false)).toBe("Unknown field 'price'.")
    expect(localExpressionError('parent.x > 1', names, false)).toContain('child table')
    expect(localExpressionError('parent.x > 1', names, true)).toBeNull()
  })
})
