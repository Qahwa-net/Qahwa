import type { DocField, DocTypeMeta } from '../api/types.ts'

/** What a client script may ask the form to do. Everything else is dropped. */
export type ScriptOp =
  | { op: 'set_value'; field: string; value: string | number | boolean | null }
  | { op: 'set_df_property'; field: string; property: 'hidden' | 'read_only' | 'reqd'; value: boolean }
  | { op: 'set_intro'; text: string }
  | { op: 'button'; id: string; label: string }
  | { op: 'msgprint'; text: string }
  | { op: 'set_row_indicator'; name: string; color: 'gray' | 'blue' | 'green' | 'orange' | 'red' | 'purple' }
  | { op: 'set_filter'; field: string; value: string | number | boolean }
  | { op: 'throw'; message: string }
  | { op: 'error'; message: string }

/**
 * The document as scripts see it: typed values (numbers are numbers, empty numbers and dates are null), without passwords, files or
 * tables. A copy: changing it does nothing until the script asks the form to.
 */
export function scriptDoc(meta: Pick<DocTypeMeta, 'fields'>, values: Record<string, string | boolean>): Record<string, unknown> {
  const doc: Record<string, unknown> = {}
  for (const f of meta.fields) {
    if (!(f.name in values) || NOT_SETTABLE.has(f.type)) continue
    const v = values[f.name]!
    if (typeof v === 'boolean') doc[f.name] = v
    else if (NUMERIC.has(f.type)) doc[f.name] = v.trim() === '' || Number.isNaN(Number(v)) ? null : Number(v)
    else doc[f.name] = v === '' && NULLABLE.has(f.type) ? null : v
  }

  return doc
}

export const MAX_OPS = 100
export const MAX_TEXT = 300
export const SCRIPT_TIMEOUT_MS = 1000

const PROPERTIES = ['hidden', 'read_only', 'reqd'] as const
const COLORS = ['gray', 'blue', 'green', 'orange', 'red', 'purple'] as const
const NOT_SETTABLE = new Set(['Table', 'Password', 'SectionBreak', 'ColumnBreak', 'TabBreak', 'Attach', 'AttachImage'])
const NUMERIC = new Set(['Int', 'Float', 'Decimal', 'Currency', 'Percent', 'Rating', 'Duration'])
const NULLABLE = new Set(['Date', 'Datetime', 'Time', 'Link'])

const text = (value: unknown, max = MAX_TEXT): string => (typeof value === 'string' ? value : String(value ?? '')).slice(0, max)

/**
 * The page never trusts what comes back from the sandbox: only these operations, only on fields the DocType has, only plain values.
 * Anything else is left out. (The server checks every saved value again, so this is about what the form shows, not about security of data.)
 */
export function sanitizeOps(raw: unknown, meta: Pick<DocTypeMeta, 'fields'>): ScriptOp[] {
  if (!Array.isArray(raw)) return []
  const fields = new Map<string, DocField>(meta.fields.map((f) => [f.name, f]))
  const result: ScriptOp[] = []
  for (const item of raw.slice(0, MAX_OPS)) {
    if (!item || typeof item !== 'object') continue
    const o = item as Record<string, unknown>
    const field = typeof o.field === 'string' ? fields.get(o.field) : undefined
    switch (o.op) {
      case 'set_value': {
        const v = o.value
        const plain = v === null || typeof v === 'boolean' || (typeof v === 'number' && Number.isFinite(v)) || (typeof v === 'string' && v.length <= 10_000)
        if (field && !NOT_SETTABLE.has(field.type) && plain) result.push({ op: 'set_value', field: field.name, value: v as string | number | boolean | null })
        break
      }
      case 'set_df_property':
        if (field && (PROPERTIES as readonly unknown[]).includes(o.property) && typeof o.value === 'boolean') {
          result.push({ op: 'set_df_property', field: field.name, property: o.property as (typeof PROPERTIES)[number], value: o.value })
        }
        break
      case 'set_intro':
        result.push({ op: 'set_intro', text: text(o.text) })
        break
      case 'button':
        if (typeof o.id === 'string' && o.id.length <= 120 && typeof o.label === 'string' && o.label.trim() !== '') result.push({ op: 'button', id: o.id, label: text(o.label, 40) })
        break
      case 'msgprint':
        result.push({ op: 'msgprint', text: text(o.text) })
        break
      case 'set_row_indicator':
        if (typeof o.name === 'string' && o.name.length <= 140 && (COLORS as readonly unknown[]).includes(o.color)) {
          result.push({ op: 'set_row_indicator', name: o.name, color: o.color as (typeof COLORS)[number] })
        }
        break
      case 'set_filter': {
        const v = o.value
        if (field && !NOT_SETTABLE.has(field.type) && (typeof v === 'boolean' || (typeof v === 'number' && Number.isFinite(v)) || (typeof v === 'string' && v.length <= 200))) {
          result.push({ op: 'set_filter', field: field.name, value: v })
        }
        break
      }
      case 'throw':
        result.push({ op: 'throw', message: text(o.message) || 'The change was refused.' })
        break
      case 'error':
        result.push({ op: 'error', message: text(o.message) })
        break
    }
  }

  return result
}

export interface WorkerLike {
  postMessage(message: unknown): void
  terminate(): void
  onmessage: ((event: { data: unknown }) => void) | null
  onerror: ((event: unknown) => void) | null
}

export interface ScriptSource {
  name: string
  title: string
  source: string
}

/**
 * Talks to the sandbox worker. One request at a time is answered within {@link SCRIPT_TIMEOUT_MS}; when a script does not answer in time
 * (it loops), the worker is ended and the scripts stay off for this form, with a message, rather than freezing anything.
 */
export class ClientScriptRunner {
  private worker: WorkerLike | null
  private nextId = 1
  private pending = new Map<number, { resolve: (value: unknown) => void; timer: ReturnType<typeof setTimeout> }>()
  private loaded: { resolve: (errors: string[]) => void; timer: ReturnType<typeof setTimeout> } | null = null
  /** Set when the sandbox had to be ended. */
  stopped: string | null = null

  private readonly meta: Pick<DocTypeMeta, 'name' | 'fields'>
  private readonly timeoutMs: number

  constructor(
    meta: Pick<DocTypeMeta, 'name' | 'fields'>,
    create: () => WorkerLike = () => new Worker('/client-script-worker.js') as unknown as WorkerLike,
    timeoutMs = SCRIPT_TIMEOUT_MS,
  ) {
    this.meta = meta
    this.timeoutMs = timeoutMs
    this.worker = create()
    this.worker.onmessage = (event) => this.receive(event.data)
    this.worker.onerror = () => this.stop('A client script could not start.')
  }

  private stop(reason: string) {
    this.stopped ??= reason
    this.worker?.terminate()
    this.worker = null
    for (const { resolve, timer } of this.pending.values()) {
      clearTimeout(timer)
      resolve(null)
    }

    this.pending.clear()
    if (this.loaded) {
      clearTimeout(this.loaded.timer)
      this.loaded.resolve([reason])
      this.loaded = null
    }
  }

  private receive(data: unknown) {
    const m = (data && typeof data === 'object' ? data : {}) as Record<string, unknown>
    if (m.type === 'loaded' && this.loaded) {
      clearTimeout(this.loaded.timer)
      const errors = Array.isArray(m.errors) ? (m.errors as { name?: unknown; message?: unknown }[]).map((e) => `${text(e.name, 80)}: ${text(e.message, 200)}`) : []
      this.loaded.resolve(errors)
      this.loaded = null
    } else if (m.type === 'result' && typeof m.id === 'number') {
      const waiting = this.pending.get(m.id)
      if (!waiting) return
      clearTimeout(waiting.timer)
      this.pending.delete(m.id)
      waiting.resolve(m.ops)
    }
  }

  /** Gives the scripts to the sandbox; resolves with a message for each script that could not even be started. */
  load(scripts: ScriptSource[]): Promise<string[]> {
    const worker = this.worker
    if (!worker) return Promise.resolve([this.stopped ?? 'Client scripts are off.'])
    return new Promise((resolve) => {
      const timer = setTimeout(() => this.stop('Client scripts took too long to start and were stopped.'), this.timeoutMs)
      this.loaded = { resolve, timer }
      worker.postMessage({ type: 'load', doctype: this.meta.name, scripts: scripts.map((s) => ({ name: s.title || s.name, source: s.source })) })
    })
  }

  private request(message: Record<string, unknown>): Promise<ScriptOp[]> {
    const worker = this.worker
    if (!worker) return Promise.resolve([])
    const id = this.nextId++
    return new Promise((resolve) => {
      const timer = setTimeout(() => this.stop('A client script took too long and was stopped. Client scripts are off for this form.'), this.timeoutMs)
      this.pending.set(id, { resolve: (ops) => resolve(sanitizeOps(ops, this.meta)), timer })
      worker.postMessage({ ...message, id })
    })
  }

  /** Runs the handlers for an event ("onload", "refresh", "validate" or a field name) on a copy of the document. */
  run(event: string, doc: Record<string, unknown>): Promise<ScriptOp[]> {
    return this.request({ type: 'event', event, doc })
  }

  /** Runs the list view's handlers ("onload" or "refresh") with copies of the rows on the page and the names ticked. */
  runList(event: string, rows: Record<string, unknown>[], selected: string[]): Promise<ScriptOp[]> {
    return this.request({ type: 'list_event', event, rows, selected })
  }

  runListButton(button: string, rows: Record<string, unknown>[], selected: string[]): Promise<ScriptOp[]> {
    return this.request({ type: 'list_button', button, rows, selected })
  }

  runButton(button: string, doc: Record<string, unknown>): Promise<ScriptOp[]> {
    return this.request({ type: 'button', button, doc })
  }

  dispose() {
    this.stop('Closed.')
  }
}
