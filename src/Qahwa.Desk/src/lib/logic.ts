import type { DocField, DocTypeMeta, ValidationRule } from '../api/types.ts'
import { Expression, type ExprContext, type Value } from './expr/expr.ts'
import { hasColumn, kindOf } from './fields.ts'
import type { CellValue, TableRow } from './tables.ts'

/** Who is editing, for functions such as user(), hasRole() and isNew(). */
export interface Session {
  user: string
  roles: string[]
  isNew: boolean
}

export interface FieldState {
  /** Shown (the "show when" condition holds). */
  active: boolean
  /** Required by the field itself or by its condition, and only while shown. Calculated fields never are. */
  required: boolean
  readOnly: boolean
}

export type Values = Record<string, CellValue>
export type Tables = Record<string, TableRow[]>

const MAX_PASSES = 3

/** What the form holds in an input becomes a value the expression language understands. Empty is null. */
export function toValue(field: DocField, input: CellValue | undefined): Value {
  if (input === undefined) return null
  switch (kindOf(field.type)) {
    case 'check':
      return input === true
    case 'number': {
      if (typeof input !== 'string' || input.trim() === '') return null
      const n = Number(input)
      return Number.isFinite(n) ? n : null
    }
    default:
      return typeof input === 'string' && input !== '' ? input : null
  }
}

/** A calculated value goes back into an input. Numbers lose floating point noise, like the server's decimal conversion. */
export function fromValue(field: DocField, value: Value): CellValue {
  if (kindOf(field.type) === 'check') return value === true
  if (value === null || typeof value === 'object') return ''
  if (typeof value === 'number') {
    if (!Number.isFinite(value)) return ''
    if (field.type === 'Int' && !Number.isInteger(value)) return ''
    return String(Number(value.toPrecision(15)))
  }
  return String(value)
}

const pad = (n: number) => String(n).padStart(2, '0')

function clock(): Pick<ExprContext, 'today' | 'now'> {
  const d = new Date()
  const today = `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`
  return { today, now: d.toISOString().replace(/\.\d{3}Z$/, 'Z') }
}

function recordValues(fields: DocField[], values: Values): Record<string, Value> {
  const record: Record<string, Value> = {}
  for (const field of fields) {
    if (hasColumn(field.type)) record[field.name] = toValue(field, values[field.name])
  }
  return record
}

/** The expression context for the document itself: its fields, plus its tables as lists of rows. */
export function documentContext(
  meta: DocTypeMeta, children: Record<string, DocTypeMeta>, values: Values, tables: Tables, session: Session,
): ExprContext {
  const fields = recordValues(meta.fields, values)
  for (const field of meta.fields.filter((f) => f.type === 'Table')) {
    const child = field.options ? children[field.options] : undefined
    if (child) fields[field.name] = (tables[field.name] ?? []).map((row) => recordValues(child.fields, row.values))
  }
  return { fields, parent: null, user: session.user, roles: session.roles, isNew: session.isNew, ...clock() }
}

/** The expression context for one row of a child table: the row's fields, with the document as `parent`. */
export function rowContext(child: DocTypeMeta, row: Values, parentValues: Values, parentMeta: DocTypeMeta, session: Session): ExprContext {
  return {
    fields: recordValues(child.fields, row),
    parent: recordValues(parentMeta.fields, parentValues),
    user: session.user,
    roles: session.roles,
    isNew: session.isNew,
    ...clock(),
  }
}

function test(source: string | null | undefined, context: ExprContext): boolean | null {
  if (!source) return null
  try {
    return Expression.parse(source).isTrue(context)
  } catch {
    return null // a broken expression is reported when the DocType is saved; the form carries on
  }
}

export function fieldState(field: DocField, context: ExprContext): FieldState {
  const active = test(field.dependsOn, context) ?? true
  const calculated = Boolean(field.formula)
  return {
    active,
    required: !calculated && active && (field.required || test(field.mandatoryDependsOn, context) === true),
    readOnly: calculated || Boolean(field.readOnly) || test(field.readOnlyDependsOn, context) === true,
  }
}

function calculate(field: DocField, context: ExprContext): CellValue | undefined {
  try {
    return fromValue(field, Expression.parse(field.formula!).evaluate(context))
  } catch {
    return undefined
  }
}

/** Formulas in dependency order, so each one is computed after the ones it reads. */
export function formulaOrder(fields: DocField[]): DocField[] {
  const formulas = fields.filter((f) => f.formula)
  const names = new Set(formulas.map((f) => f.name))
  const deps = new Map<string, string[]>()
  for (const f of formulas) {
    try {
      deps.set(f.name, [...Expression.parse(f.formula!).analysis.identifiers].filter((n) => names.has(n)))
    } catch {
      deps.set(f.name, [])
    }
  }

  const ordered: DocField[] = []
  const placed = new Set<string>()
  let pending = [...formulas]
  while (pending.length > 0) {
    let ready = pending.filter((f) => deps.get(f.name)!.every((d) => placed.has(d)))
    if (ready.length === 0) ready = [pending[0]!] // a cycle: the server refuses to save such a DocType
    for (const f of ready) {
      ordered.push(f)
      placed.add(f.name)
    }
    pending = pending.filter((f) => !ready.includes(f))
  }
  return ordered
}

/**
 * Calculates every formula of the document and of its table rows from what the user has typed. It is pure and
 * can be run on every render: calculated values are derived, never stored, so editing a formula's inputs always
 * updates them.
 */
export function applyLogic(
  meta: DocTypeMeta, children: Record<string, DocTypeMeta>, values: Values, tables: Tables, session: Session,
): { values: Values; tables: Tables } {
  const rowFormulas = meta.fields
    .filter((f) => f.type === 'Table' && f.options && children[f.options])
    .map((f) => ({ field: f, child: children[f.options!]! }))
    .filter(({ child }) => child.fields.some((c) => c.formula))
  const docFormulas = formulaOrder(meta.fields)
  if (rowFormulas.length === 0 && docFormulas.length === 0) return { values, tables }

  let nextValues = values
  let nextTables = tables

  for (let pass = 0; pass < MAX_PASSES; pass++) {
    let changed = false

    for (const { field, child } of rowFormulas) {
      const order = formulaOrder(child.fields)
      const rows = (nextTables[field.name] ?? []).map((row) => {
        let current = row
        for (const formula of order) {
          const result = calculate(formula, rowContext(child, current.values, nextValues, meta, session))
          if (result !== undefined && result !== current.values[formula.name]) {
            current = { ...current, values: { ...current.values, [formula.name]: result } }
            changed = true
          }
        }
        return current
      })
      if (rows.some((row, i) => row !== (nextTables[field.name] ?? [])[i])) nextTables = { ...nextTables, [field.name]: rows }
    }

    for (const formula of docFormulas) {
      const result = calculate(formula, documentContext(meta, children, nextValues, nextTables, session))
      if (result !== undefined && result !== nextValues[formula.name]) {
        nextValues = { ...nextValues, [formula.name]: result }
        changed = true
      }
    }

    if (!changed) break
  }

  return { values: nextValues, tables: nextTables }
}

/** For a new document: fill empty fields whose default is an expression, e.g. "=today()". */
export function expressionDefaults(
  meta: DocTypeMeta, children: Record<string, DocTypeMeta>, values: Values, session: Session,
): Values {
  const context = documentContext(meta, children, values, {}, session)
  let next = values
  for (const field of meta.fields) {
    if (!field.default || field.default.length < 2 || field.default[0] !== '=' || !hasColumn(field.type)) continue
    const current = next[field.name]
    if (current !== undefined && current !== '' && current !== false) continue
    try {
      const result = fromValue(field, Expression.parse(field.default.slice(1)).evaluate(context))
      if (result !== '' && result !== false) next = { ...next, [field.name]: result }
    } catch {
      // reported when the DocType is saved
    }
  }
  return next
}

/** The same for a new table row: defaults that read the document (`parent.x`) or the session. */
export function rowExpressionDefaults(
  child: DocTypeMeta, row: Values, parentValues: Values, parentMeta: DocTypeMeta, session: Session,
): Values {
  const context = rowContext(child, row, parentValues, parentMeta, session)
  let next = row
  for (const field of child.fields) {
    if (!field.default || field.default.length < 2 || field.default[0] !== '=' || !hasColumn(field.type)) continue
    const current = next[field.name]
    if (current !== undefined && current !== '' && current !== false) continue
    try {
      const result = fromValue(field, Expression.parse(field.default.slice(1)).evaluate(context))
      if (result !== '' && result !== false) next = { ...next, [field.name]: result }
    } catch {
      // reported when the DocType is saved
    }
  }
  return next
}

/** Fields that copy a value from the document a Link field points at ("fetch from"). */
export function fetchTargets(meta: DocTypeMeta, linkName: string): { field: DocField; source: string }[] {
  return meta.fields
    .filter((f) => f.fetchFrom?.startsWith(`${linkName}.`))
    .map((field) => ({ field, source: field.fetchFrom!.slice(linkName.length + 1) }))
}

/** The validation rules that do not hold for this data: what would stop a save. */
export function failingRules(meta: DocTypeMeta, context: ExprContext): ValidationRule[] {
  return (meta.validations ?? []).filter((rule) => {
    try {
      return !Expression.parse(rule.expression).isTrue(context)
    } catch {
      return false // a broken rule is reported when the DocType is saved
    }
  })
}
