import { describe, expect, it } from 'vitest'
import vectorsText from '../../../../../tests/expression-vectors/vectors.json?raw'
import { Expression, ExpressionError, checkExpression, equal, truthy, toText, type ExprContext, type Value } from './expr.ts'

interface Case {
  expr: string
  fields?: Record<string, Value>
  parent?: Record<string, Value>
  user?: string
  roles?: string[]
  isNew?: boolean
  result?: Value
  error?: boolean
}

const vectors = JSON.parse(vectorsText) as { env: { today: string; now: string; user: string; roles: string[]; isNew: boolean }; cases: Case[] }

function context(test: Case): ExprContext {
  return {
    fields: test.fields ?? {},
    parent: test.parent ?? null,
    user: test.user ?? vectors.env.user,
    roles: test.roles ?? vectors.env.roles,
    isNew: test.isNew ?? vectors.env.isNew,
    today: vectors.env.today,
    now: vectors.env.now,
  }
}

/** Compares results, treating -0 and 0 as the same number. */
function same(a: Value, b: Value): boolean {
  if (Array.isArray(a) && Array.isArray(b)) return a.length === b.length && a.every((x, i) => same(x, b[i]!))
  if (a !== null && b !== null && typeof a === 'object' && typeof b === 'object' && !Array.isArray(a) && !Array.isArray(b)) {
    const keys = Object.keys(a)
    return keys.length === Object.keys(b).length && keys.every((k) => k in b && same(a[k]!, b[k]!))
  }
  return equal(a, b)
}

describe('the shared expression vectors', () => {
  it('has a meaningful number of cases', () => expect(vectors.cases.length).toBeGreaterThan(200))

  it('gives exactly the results the server gives', () => {
    const failures: string[] = []
    for (const test of vectors.cases) {
      try {
        const actual = Expression.parse(test.expr).evaluate(context(test))
        if (test.error) failures.push(`[${test.expr}] should not parse but gave ${JSON.stringify(actual)}`)
        else if (!same(actual, test.result ?? null)) {
          failures.push(`[${test.expr}] expected ${JSON.stringify(test.result)} but got ${JSON.stringify(actual)}`)
        }
      } catch (error) {
        if (!(error instanceof ExpressionError)) throw error
        if (!test.error) failures.push(`[${test.expr}] failed to parse: ${error.message}`)
      }
    }
    expect(failures).toEqual([])
  })
})

describe('parsing', () => {
  it('reports what and where', () => {
    const error = checkExpression('qty * (rate + ')
    expect(error?.message).toContain('end')
    expect(error?.position).toBe(14)
    expect(checkExpression('nosuch(1)')?.message).toContain("Unknown function 'nosuch'")
    expect(checkExpression('round()')?.message).toContain('takes 1 to 2')
    expect(checkExpression('a + b $ c')?.position).toBe(6)
    expect(checkExpression('a + b')).toBeNull()
  })

  it('refuses runaway expressions', () => {
    expect(checkExpression('1'.repeat(1001))).not.toBeNull()
    expect(checkExpression(Array(300).fill('1').join(' + '))).not.toBeNull()
    expect(checkExpression('!'.repeat(60) + '1')).not.toBeNull()
    expect(checkExpression(Array(50).fill('1').join(' + '))).toBeNull()
  })

  it('lists the fields, functions and parent members an expression uses', () => {
    const { analysis } = Expression.parse("round(sum(items, 'amount') * (1 - parent.discount) + tax, 2) > limit && parent.region == 'EU'")
    expect([...analysis.identifiers].sort()).toEqual(['items', 'limit', 'tax'])
    expect([...analysis.parentMembers].sort()).toEqual(['discount', 'region'])
    expect(analysis.usesParent).toBe(true)
    expect(analysis.functions.has('sum')).toBe(true)
    expect(Expression.parse('a + b').analysis.usesParent).toBe(false)
  })

  it('only ever throws ExpressionError, whatever it is given', () => {
    let seed = 7
    const random = (n: number) => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed % n }
    const alphabet = "ab1 .'\"()[]+-*/%<>=!&|?:,\\\n\tin_é9"
    for (let i = 0; i < 3000; i++) {
      const text = Array.from({ length: random(40) + 1 }, () => alphabet[random(alphabet.length)]).join('')
      try {
        Expression.parse(text)
      } catch (error) {
        expect(error).toBeInstanceOf(ExpressionError)
      }
    }
  })
})

describe('evaluation', () => {
  it('never throws on any combination of values', () => {
    const expressions = ['a + b', 'a * b / c', 'a < b', 'sum(a, b)', 'len(a) + count(b)', 'addDays(a, b)', 'daysBetween(a, b)',
      'round(a, b)', 'a.b.c', 'a in b', 'contains(a, b)', 'min(a, b)', 'number(a) + 1', 'upper(a) + lower(b)']
    const shapes: Value[] = [null, true, 0, -1, 1e300, NaN, Infinity, '', 'text', '2026-03-09', [1, 'a', null], { b: 1 }]
    for (const source of expressions) {
      const expression = Expression.parse(source)
      for (const a of shapes) for (const b of shapes) {
        const ctx: ExprContext = { fields: { a, b, c: a }, user: '', roles: [], isNew: false, today: '2026-03-09', now: '' }
        expression.evaluate(ctx)
        expression.isTrue(ctx)
      }
    }
  })

  it('follows the documented truthiness and text rules', () => {
    expect([null, 0, '', []].map((v) => truthy(v))).toEqual([false, false, false, false])
    expect([truthy('0'), truthy({})]).toEqual([true, true])
    expect([toText(12), toText(0.1), toText(true), toText(null)]).toEqual(['12', '0.1', 'true', ''])
  })
})
