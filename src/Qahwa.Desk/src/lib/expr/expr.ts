/**
 * The expression language used for field logic (conditions, formulas, defaults and rules).
 *
 * This is the TypeScript twin of Qahwa.Core.Expressions on the server. Both are checked against the same
 * vectors in tests/expression-vectors/vectors.json, so a rule gives the same answer in the form and on save.
 * Evaluation never throws: operations on the wrong kind of value give null or false.
 */

export type Value = null | boolean | number | string | Value[] | { [key: string]: Value }

export interface ExprContext {
  fields: Record<string, Value>
  /** For a row of a child table: the document it belongs to, reachable as `parent`. */
  parent?: Record<string, Value> | null
  user: string
  roles: string[]
  isNew: boolean
  /** `yyyy-MM-dd` */
  today: string
  /** ISO 8601, UTC */
  now: string
  /** For `lookup(link_field, 'field')`: the fields of the document each Link field points at, by Link field name. */
  linked?: Record<string, Record<string, Value>> | null
}

export class ExpressionError extends Error {
  position: number

  constructor(message: string, position: number) {
    super(message)
    this.name = 'ExpressionError'
    this.position = position
  }
}

type Node =
  | { t: 'lit'; value: Value }
  | { t: 'id'; name: string }
  | { t: 'member'; target: Node; name: string }
  | { t: 'unary'; op: string; operand: Node }
  | { t: 'bin'; op: string; left: Node; right: Node }
  | { t: 'logic'; op: string; left: Node; right: Node }
  | { t: 'ternary'; cond: Node; then: Node; else: Node }
  | { t: 'call'; name: string; args: Node[] }
  | { t: 'list'; items: Node[] }

const MAX_LENGTH = 1000
const MAX_DEPTH = 40
const MAX_NODES = 400

/** Function name to [minimum, maximum] argument count. */
export const FUNCTIONS: Record<string, [number, number]> = {
  empty: [1, 1], len: [1, 1], lower: [1, 1], upper: [1, 1], trim: [1, 1],
  contains: [2, 2], startsWith: [2, 2], endsWith: [2, 2],
  round: [1, 2], abs: [1, 1], floor: [1, 1], ceil: [1, 1], min: [1, 8], max: [1, 8],
  sum: [1, 2], count: [1, 1],
  lookup: [2, 2], avg: [2, 2], minOf: [2, 2], maxOf: [2, 2], countIf: [3, 3], sumIf: [4, 4], distinct: [1, 2],
  today: [0, 0], now: [0, 0], user: [0, 0], hasRole: [1, 1], isNew: [0, 0],
  text: [1, 1], number: [1, 1],
  addDays: [2, 2], daysBetween: [2, 2],
}

const PUNCTUATION = [
  '==', '!=', '<=', '>=', '&&', '||', '??',
  '+', '-', '*', '/', '%', '<', '>', '!', '?', ':', '(', ')', '[', ']', ',', '.',
]

interface Token {
  kind: 'number' | 'string' | 'ident' | 'punct' | 'end'
  text: string
  position: number
  number: number
}

const isDigit = (c: string | undefined) => c !== undefined && c >= '0' && c <= '9'
const isIdentStart = (c: string | undefined) => c !== undefined && ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c === '_')

function tokenize(source: string): Token[] {
  const tokens: Token[] = []
  let i = 0

  while (i < source.length) {
    const c = source[i]!
    if (c === ' ' || c === '\t' || c === '\r' || c === '\n') {
      i++
      continue
    }

    if (isDigit(c)) {
      const start = i
      while (isDigit(source[i])) i++
      if (source[i] === '.' && isDigit(source[i + 1])) {
        i++
        while (isDigit(source[i])) i++
      }
      const text = source.slice(start, i)
      tokens.push({ kind: 'number', text, position: start, number: Number(text) })
      continue
    }

    if (isIdentStart(c)) {
      const start = i
      while (isIdentStart(source[i]) || isDigit(source[i])) i++
      tokens.push({ kind: 'ident', text: source.slice(start, i), position: start, number: 0 })
      continue
    }

    if (c === "'" || c === '"') {
      const start = i
      let text = ''
      let closed = false
      i++
      while (i < source.length) {
        const ch = source[i++]!
        if (ch === c) {
          closed = true
          break
        }
        if (ch !== '\\') {
          text += ch
          continue
        }
        if (i >= source.length) break
        const escaped = source[i++]!
        const map: Record<string, string> = { '\\': '\\', "'": "'", '"': '"', n: '\n', t: '\t', r: '\r' }
        const mapped = map[escaped]
        if (mapped === undefined) throw new ExpressionError(`Unknown escape '\\${escaped}'.`, i - 2)
        text += mapped
      }
      if (!closed) throw new ExpressionError('The text is missing its closing quote.', start)
      tokens.push({ kind: 'string', text, position: start, number: 0 })
      continue
    }

    const matched = PUNCTUATION.find((p) => source.startsWith(p, i))
    if (!matched) throw new ExpressionError(`Unexpected character '${c}'.`, i)
    tokens.push({ kind: 'punct', text: matched, position: i, number: 0 })
    i += matched.length
  }

  tokens.push({ kind: 'end', text: '', position: source.length, number: 0 })
  return tokens
}

class Parser {
  private tokens: Token[]
  private index = 0
  private depth = 0
  private nodes = 0

  constructor(source: string) {
    this.tokens = tokenize(source)
  }

  parse(): Node {
    const node = this.ternary()
    if (this.peek.kind !== 'end') throw new ExpressionError(`Unexpected '${this.peek.text}'.`, this.peek.position)
    return node
  }

  private get peek(): Token {
    return this.tokens[this.index]!
  }

  private next(): Token {
    return this.tokens[this.index++]!
  }

  private isPunct(text: string) {
    return this.peek.kind === 'punct' && this.peek.text === text
  }

  private isKeyword(text: string) {
    return this.peek.kind === 'ident' && this.peek.text === text
  }

  private expect(text: string) {
    if (!this.isPunct(text)) {
      const found = this.peek.kind === 'end' ? 'the end' : this.peek.text
      throw new ExpressionError(`Expected '${text}' but found '${found}'.`, this.peek.position)
    }
    this.index++
  }

  private count<T extends Node>(node: T): T {
    if (++this.nodes > MAX_NODES) throw new ExpressionError('The expression is too complex.', this.peek.position)
    return node
  }

  private ternary(): Node {
    if (++this.depth > MAX_DEPTH) throw new ExpressionError('The expression is nested too deeply.', this.peek.position)
    const cond = this.coalesce()
    let result = cond
    if (this.isPunct('?')) {
      this.index++
      const then = this.ternary()
      this.expect(':')
      const otherwise = this.ternary()
      result = this.count({ t: 'ternary', cond, then, else: otherwise })
    }
    this.depth--
    return result
  }

  private coalesce(): Node {
    let left = this.or()
    while (this.isPunct('??')) {
      this.index++
      left = this.count({ t: 'logic', op: '??', left, right: this.or() })
    }
    return left
  }

  private or(): Node {
    let left = this.and()
    while (this.isPunct('||')) {
      this.index++
      left = this.count({ t: 'logic', op: '||', left, right: this.and() })
    }
    return left
  }

  private and(): Node {
    let left = this.equality()
    while (this.isPunct('&&')) {
      this.index++
      left = this.count({ t: 'logic', op: '&&', left, right: this.equality() })
    }
    return left
  }

  private equality(): Node {
    let left = this.relational()
    while (this.isPunct('==') || this.isPunct('!=')) {
      const op = this.next().text
      left = this.count({ t: 'bin', op, left, right: this.relational() })
    }
    return left
  }

  private relational(): Node {
    let left = this.additive()
    for (;;) {
      if (this.isPunct('<') || this.isPunct('<=') || this.isPunct('>') || this.isPunct('>=')) {
        const op = this.next().text
        left = this.count({ t: 'bin', op, left, right: this.additive() })
      } else if (this.isKeyword('in')) {
        this.index++
        left = this.count({ t: 'bin', op: 'in', left, right: this.additive() })
      } else {
        return left
      }
    }
  }

  private additive(): Node {
    let left = this.multiplicative()
    while (this.isPunct('+') || this.isPunct('-')) {
      const op = this.next().text
      left = this.count({ t: 'bin', op, left, right: this.multiplicative() })
    }
    return left
  }

  private multiplicative(): Node {
    let left = this.unary()
    while (this.isPunct('*') || this.isPunct('/') || this.isPunct('%')) {
      const op = this.next().text
      left = this.count({ t: 'bin', op, left, right: this.unary() })
    }
    return left
  }

  private unary(): Node {
    if (this.isPunct('!') || this.isPunct('-')) {
      if (++this.depth > MAX_DEPTH) throw new ExpressionError('The expression is nested too deeply.', this.peek.position)
      const op = this.next().text
      const node = this.count({ t: 'unary', op, operand: this.unary() } as Node)
      this.depth--
      return node
    }
    return this.postfix()
  }

  private postfix(): Node {
    let node = this.primary()
    while (this.isPunct('.')) {
      this.index++
      if (this.peek.kind !== 'ident') throw new ExpressionError("Expected a field name after '.'.", this.peek.position)
      node = this.count({ t: 'member', target: node, name: this.next().text })
    }
    return node
  }

  private primary(): Node {
    const token = this.next()
    switch (token.kind) {
      case 'number':
        return this.count({ t: 'lit', value: token.number })
      case 'string':
        return this.count({ t: 'lit', value: token.text })
      case 'ident':
        if (token.text === 'true') return this.count({ t: 'lit', value: true })
        if (token.text === 'false') return this.count({ t: 'lit', value: false })
        if (token.text === 'null') return this.count({ t: 'lit', value: null })
        if (token.text === 'in') throw new ExpressionError("Unexpected 'in'.", token.position)
        if (this.isPunct('(')) return this.call(token)
        return this.count({ t: 'id', name: token.text })
      case 'punct':
        if (token.text === '(') {
          const inner = this.ternary()
          this.expect(')')
          return inner
        }
        if (token.text === '[') {
          const items: Node[] = []
          if (!this.isPunct(']')) {
            do items.push(this.ternary())
            while (this.isPunct(',') && ++this.index > 0)
          }
          this.expect(']')
          return this.count({ t: 'list', items })
        }
        throw new ExpressionError(`Unexpected '${token.text}'.`, token.position)
      default:
        throw new ExpressionError('Unexpected end of the expression.', token.position)
    }
  }

  private call(name: Token): Node {
    const arity = FUNCTIONS[name.text]
    if (!arity) throw new ExpressionError(`Unknown function '${name.text}'.`, name.position)

    this.expect('(')
    const args: Node[] = []
    if (!this.isPunct(')')) {
      do args.push(this.ternary())
      while (this.isPunct(',') && ++this.index > 0)
    }
    this.expect(')')
    if (args.length < arity[0] || args.length > arity[1]) {
      const expected = arity[0] === arity[1] ? `${arity[0]}` : `${arity[0]} to ${arity[1]}`
      throw new ExpressionError(`${name.text}() takes ${expected} argument(s), not ${args.length}.`, name.position)
    }
    if (name.text === 'lookup' && !(args[0]!.t === 'id' && args[0]!.name !== 'parent' && args[1]!.t === 'lit' && typeof args[1]!.value === 'string')) {
      throw new ExpressionError("lookup() needs a Link field and the name of a field, as in lookup(customer, 'credit_limit').", name.position)
    }
    return this.count({ t: 'call', name: name.text, args })
  }
}

// --- values ---------------------------------------------------------------------------------------------------

export function truthy(value: Value | undefined): boolean {
  if (value === null || value === undefined) return false
  if (typeof value === 'boolean') return value
  if (typeof value === 'number') return value !== 0 && !Number.isNaN(value)
  if (typeof value === 'string') return value.length > 0
  if (Array.isArray(value)) return value.length > 0
  return true
}

export function equal(a: Value | undefined, b: Value | undefined): boolean {
  if (a === null || a === undefined) return b === null || b === undefined
  if (typeof a === 'boolean') return typeof b === 'boolean' && a === b
  if (typeof a === 'number') return typeof b === 'number' && a === b
  if (typeof a === 'string') return typeof b === 'string' && a === b
  return false
}

/** How a value becomes text when joined to a string or passed to text(). */
export function toText(value: Value | undefined): string {
  if (value === null || value === undefined) return ''
  if (typeof value === 'boolean') return value ? 'true' : 'false'
  if (typeof value === 'number') return String(value)
  if (typeof value === 'string') return value
  return ''
}

function ascii(text: string, upper: boolean): string {
  let out = ''
  for (const ch of text) {
    const code = ch.charCodeAt(0)
    if (upper && ch.length === 1 && code >= 97 && code <= 122) out += String.fromCharCode(code - 32)
    else if (!upper && ch.length === 1 && code >= 65 && code <= 90) out += String.fromCharCode(code + 32)
    else out += ch
  }
  return out
}

/** Days since 1970-01-01 for a proleptic Gregorian date (Howard Hinnant's algorithm, identical on the server). */
function daysFromCivil(y: number, m: number, d: number): number {
  y -= m <= 2 ? 1 : 0
  const era = Math.floor(y / 400)
  const yoe = y - era * 400
  const doy = Math.floor((153 * (m + (m > 2 ? -3 : 9)) + 2) / 5) + d - 1
  const doe = yoe * 365 + Math.floor(yoe / 4) - Math.floor(yoe / 100) + doy
  return era * 146097 + doe - 719468
}

function civilFromDays(days: number): [number, number, number] {
  const z = days + 719468
  const era = Math.floor(z / 146097)
  const doe = z - era * 146097
  const yoe = Math.floor((doe - Math.floor(doe / 1460) + Math.floor(doe / 36524) - Math.floor(doe / 146096)) / 365)
  const y = yoe + era * 400
  const doy = doe - (365 * yoe + Math.floor(yoe / 4) - Math.floor(yoe / 100))
  const mp = Math.floor((5 * doy + 2) / 153)
  const d = doy - Math.floor((153 * mp + 2) / 5) + 1
  const m = mp + (mp < 10 ? 3 : -9)
  return [y + (m <= 2 ? 1 : 0), m, d]
}

function parseDate(value: Value | undefined): number | null {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}/.test(value)) return null
  const y = Number(value.slice(0, 4))
  const m = Number(value.slice(5, 7))
  const d = Number(value.slice(8, 10))
  if (m < 1 || m > 12 || d < 1) return null
  const days = daysFromCivil(y, m, d)
  const [by, bm, bd] = civilFromDays(days)
  return by === y && bm === m && bd === d ? days : null // rejects 2026-02-30 and the like
}

function formatDate(days: number): string {
  const [y, m, d] = civilFromDays(days)
  return `${String(y).padStart(4, '0')}-${String(m).padStart(2, '0')}-${String(d).padStart(2, '0')}`
}

// --- evaluation -----------------------------------------------------------------------------------------------

function compare(left: Value, right: Value): number | null {
  if (typeof left === 'number' && typeof right === 'number') {
    if (Number.isNaN(left) || Number.isNaN(right)) return null
    return left < right ? -1 : left > right ? 1 : 0
  }
  if (typeof left === 'string' && typeof right === 'string') return left < right ? -1 : left > right ? 1 : 0
  return null
}

function binary(op: string, left: Value, right: Value): Value {
  switch (op) {
    case '==': return equal(left, right)
    case '!=': return !equal(left, right)
    case 'in': return Array.isArray(right) && right.some((item) => equal(left, item))
    case '+':
      if (typeof left === 'number' && typeof right === 'number') return left + right
      if (typeof left === 'string' || typeof right === 'string') return toText(left) + toText(right)
      return null
    case '<': case '<=': case '>': case '>=': {
      const order = compare(left, right)
      if (order === null) return false
      return op === '<' ? order < 0 : op === '<=' ? order <= 0 : op === '>' ? order > 0 : order >= 0
    }
  }

  if (typeof left !== 'number' || typeof right !== 'number') return null
  switch (op) {
    case '-': return left - right
    case '*': return left * right
    case '/': return right === 0 ? null : left / right
    case '%': return right === 0 ? null : left % right
    default: return null
  }
}

function invoke(name: string, args: Value[], ctx: ExprContext): Value {
  const a = args[0] ?? null
  const b = args[1] ?? null

  switch (name) {
    case 'empty':
      return a === null || (typeof a === 'string' && a.length === 0) || (Array.isArray(a) && a.length === 0)
    case 'len':
      return typeof a === 'string' ? a.length : Array.isArray(a) ? a.length : 0
    case 'lower': return typeof a === 'string' ? ascii(a, false) : null
    case 'upper': return typeof a === 'string' ? ascii(a, true) : null
    case 'trim': return typeof a === 'string' ? a.replace(/^[ \t\r\n]+|[ \t\r\n]+$/g, '') : null
    case 'contains':
      if (typeof a === 'string') return typeof b === 'string' && a.includes(b)
      if (Array.isArray(a)) return a.some((item) => equal(item, b))
      return false
    case 'startsWith': return typeof a === 'string' && typeof b === 'string' && a.startsWith(b)
    case 'endsWith': return typeof a === 'string' && typeof b === 'string' && a.endsWith(b)
    case 'round': {
      if (typeof a !== 'number') return null
      const digits = typeof b === 'number' ? Math.min(9, Math.max(0, Math.trunc(b))) : 0
      const power = Math.pow(10, digits)
      const scaled = Math.floor(Math.abs(a) * power + 0.5) / power
      return a < 0 ? -scaled : scaled
    }
    case 'abs': return typeof a === 'number' ? Math.abs(a) : null
    case 'floor': return typeof a === 'number' ? Math.floor(a) : null
    case 'ceil': return typeof a === 'number' ? Math.ceil(a) : null
    case 'min': case 'max': {
      if (args.some((x) => typeof x !== 'number')) return null
      const numbers = args as number[]
      return name === 'min' ? Math.min(...numbers) : Math.max(...numbers)
    }
    case 'sum': {
      if (!Array.isArray(a)) return null
      let total = 0
      for (const row of a) {
        const item = typeof b === 'string' ? (row !== null && typeof row === 'object' && !Array.isArray(row) ? (row[b] ?? null) : null) : row
        if (typeof item === 'number') total += item
      }
      return total
    }
    case 'count': return Array.isArray(a) ? a.length : 0
    case 'avg': case 'minOf': case 'maxOf': {
      if (!Array.isArray(a) || typeof b !== 'string') return null
      let seen = 0, sum = 0, low = 0, high = 0
      for (const row of a) {
        const value = row !== null && typeof row === 'object' && !Array.isArray(row) ? (row[b] ?? null) : null
        if (typeof value !== 'number') continue
        low = seen === 0 ? value : Math.min(low, value)
        high = seen === 0 ? value : Math.max(high, value)
        sum += value
        seen++
      }
      return seen === 0 ? null : name === 'avg' ? sum / seen : name === 'minOf' ? low : high
    }
    case 'countIf': {
      if (!Array.isArray(a) || typeof b !== 'string') return null
      let matching = 0
      for (const row of a) {
        const value = row !== null && typeof row === 'object' && !Array.isArray(row) ? (row[b] ?? null) : null
        if (equal(value, args[2] ?? null)) matching++
      }
      return matching
    }
    case 'sumIf': {
      if (!Array.isArray(a) || typeof b !== 'string' || typeof args[2] !== 'string') return null
      let added = 0
      for (const row of a) {
        const fields = row !== null && typeof row === 'object' && !Array.isArray(row) ? row : null
        const amount = fields ? (fields[b] ?? null) : null
        if (fields && equal(fields[args[2]] ?? null, args[3] ?? null) && typeof amount === 'number') added += amount
      }
      return added
    }
    case 'distinct': {
      if (!Array.isArray(a)) return null
      const unique: Value[] = []
      for (const row of a) {
        const item = typeof b === 'string' ? (row !== null && typeof row === 'object' && !Array.isArray(row) ? (row[b] ?? null) : null) : row
        if (item !== null && !unique.some((u) => equal(u, item))) unique.push(item)
      }
      return unique
    }
    case 'today': return ctx.today
    case 'now': return ctx.now
    case 'user': return ctx.user
    case 'hasRole': return typeof a === 'string' && ctx.roles.includes(a)
    case 'isNew': return ctx.isNew
    case 'text': return toText(a)
    case 'number':
      if (typeof a === 'number') return a
      return typeof a === 'string' && /^-?\d+(\.\d+)?$/.test(a) ? Number(a) : null
    case 'addDays': {
      const start = parseDate(a)
      return start !== null && typeof b === 'number' && Number.isFinite(b) ? formatDate(start + Math.trunc(b)) : null
    }
    case 'daysBetween': {
      const from = parseDate(a)
      const to = parseDate(b)
      return from !== null && to !== null ? to - from : null
    }
    default: return null
  }
}

function evaluate(node: Node, ctx: ExprContext): Value {
  switch (node.t) {
    case 'lit': return node.value
    case 'id':
      if (node.name === 'parent') return ctx.parent ?? null
      return ctx.fields[node.name] ?? null
    case 'member': {
      const target = evaluate(node.target, ctx)
      return target !== null && typeof target === 'object' && !Array.isArray(target) ? (target[node.name] ?? null) : null
    }
    case 'unary': {
      const value = evaluate(node.operand, ctx)
      return node.op === '!' ? !truthy(value) : typeof value === 'number' ? -value : null
    }
    case 'logic':
      if (node.op === '&&') return truthy(evaluate(node.left, ctx)) && truthy(evaluate(node.right, ctx))
      if (node.op === '||') return truthy(evaluate(node.left, ctx)) || truthy(evaluate(node.right, ctx))
      return evaluate(node.left, ctx) ?? evaluate(node.right, ctx)
    case 'ternary': return truthy(evaluate(node.cond, ctx)) ? evaluate(node.then, ctx) : evaluate(node.else, ctx)
    case 'list': return node.items.map((item) => evaluate(item, ctx))
    case 'bin': return binary(node.op, evaluate(node.left, ctx), evaluate(node.right, ctx))
    case 'call': {
      if (node.name === 'lookup' && node.args[0]!.t === 'id' && node.args[1]!.t === 'lit' && typeof node.args[1]!.value === 'string') {
        const linked = ctx.linked?.[node.args[0]!.name]
        return linked ? (linked[node.args[1]!.value] ?? null) : null
      }
      return invoke(node.name, node.args.map((arg) => evaluate(arg, ctx)), ctx)
    }
  }
}

function walk(node: Node, found: Analysis): void {
  switch (node.t) {
    case 'id':
      if (node.name === 'parent') found.usesParent = true
      else found.identifiers.add(node.name)
      break
    case 'member':
      if (node.target.t === 'id' && node.target.name === 'parent') {
        found.usesParent = true
        found.parentMembers.add(node.name)
      } else {
        walk(node.target, found)
      }
      break
    case 'unary': walk(node.operand, found); break
    case 'bin': case 'logic': walk(node.left, found); walk(node.right, found); break
    case 'ternary': walk(node.cond, found); walk(node.then, found); walk(node.else, found); break
    case 'call':
      found.functions.add(node.name)
      if (node.name === 'lookup' && node.args[0]!.t === 'id' && node.args[1]!.t === 'lit' && typeof node.args[1]!.value === 'string') found.lookups.push([node.args[0]!.name, node.args[1]!.value])
      node.args.forEach((arg) => walk(arg, found))
      break
    case 'list': node.items.forEach((item) => walk(item, found)); break
    default: break
  }
}

export interface Analysis {
  /** Every `lookup(link_field, 'field')`, as [link field, field of the linked document]. */
  lookups: Array<[string, string]>
  identifiers: Set<string>
  parentMembers: Set<string>
  functions: Set<string>
  usesParent: boolean
}

export class Expression {
  readonly source: string
  readonly analysis: Analysis
  private readonly root: Node

  private constructor(source: string, root: Node) {
    this.source = source
    this.root = root
    this.analysis = { lookups: [], identifiers: new Set(), parentMembers: new Set(), functions: new Set(), usesParent: false }
    walk(root, this.analysis)
  }

  /** @throws ExpressionError when the source is not a valid expression. */
  static parse(source: string): Expression {
    const cached = cache.get(source)
    if (cached) return cached

    if (source.trim() === '') throw new ExpressionError('The expression is empty.', 0)
    if (source.length > MAX_LENGTH) throw new ExpressionError(`The expression is too long (max ${MAX_LENGTH} characters).`, MAX_LENGTH)

    const expression = new Expression(source, new Parser(source).parse())
    if (cache.size >= 5000) cache.clear()
    cache.set(source, expression)
    return expression
  }

  evaluate(ctx: ExprContext): Value {
    return evaluate(this.root, ctx)
  }

  isTrue(ctx: ExprContext): boolean {
    return truthy(this.evaluate(ctx))
  }
}

const cache = new Map<string, Expression>()

/** The error message for an invalid expression, or null when it is valid. */
export function checkExpression(source: string): { message: string; position: number } | null {
  try {
    Expression.parse(source)
    return null
  } catch (error) {
    if (error instanceof ExpressionError) return { message: error.message, position: error.position }
    throw error
  }
}
