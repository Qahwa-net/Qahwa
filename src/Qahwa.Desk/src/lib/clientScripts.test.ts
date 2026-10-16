import { describe, expect, it, vi } from 'vitest'
import type { DocField } from '../api/types.ts'
import { ClientScriptRunner, sanitizeOps, scriptDoc, type WorkerLike } from './clientScripts.ts'

const field = (name: string, type = 'Data'): DocField => ({ name, type, label: name } as unknown as DocField)
const meta = { name: 'Ticket', fields: [field('status'), field('amount', 'Float'), field('pin', 'Password'), field('lines', 'Table'), field('sec', 'SectionBreak')] }

describe('operations from the sandbox', () => {
  it('keeps the allowed operations on real fields', () => {
    const ops = sanitizeOps([
      { op: 'set_value', field: 'status', value: 'Open' },
      { op: 'set_value', field: 'amount', value: 5 },
      { op: 'set_df_property', field: 'status', property: 'hidden', value: true },
      { op: 'set_intro', text: 'Hello' },
      { op: 'button', id: 'b0:Go', label: 'Go' },
      { op: 'msgprint', text: 'Saved' },
      { op: 'throw', message: 'No' },
    ], meta)
    expect(ops.map((o) => o.op)).toEqual(['set_value', 'set_value', 'set_df_property', 'set_intro', 'button', 'msgprint', 'throw'])
  })

  it('drops anything else', () => {
    const ops = sanitizeOps([
      { op: 'set_value', field: 'nope', value: 'x' }, // not a field
      { op: 'set_value', field: 'pin', value: 'x' }, // a password
      { op: 'set_value', field: 'lines', value: 'x' }, // a table
      { op: 'set_value', field: 'sec', value: 'x' }, // a layout field
      { op: 'set_value', field: 'status', value: { a: 1 } }, // not a plain value
      { op: 'set_value', field: 'amount', value: Number.NaN },
      { op: 'set_value', field: 'status', value: 'x'.repeat(10_001) },
      { op: 'set_df_property', field: 'status', property: 'label', value: true }, // not allowed
      { op: 'set_df_property', field: 'status', property: 'hidden', value: 'yes' },
      { op: 'eval', code: 'alert(1)' },
      { op: 'button', id: 'x', label: '   ' },
      null, 5, 'text', [],
    ], meta)
    expect(ops).toEqual([])
    expect(sanitizeOps('not a list', meta)).toEqual([])
    expect(sanitizeOps(undefined, meta)).toEqual([])
  })

  it('limits how many and how long', () => {
    const many = Array.from({ length: 500 }, () => ({ op: 'set_intro', text: 'x'.repeat(1000) }))
    const ops = sanitizeOps(many, meta)
    expect(ops).toHaveLength(100)
    expect((ops[0] as { text: string }).text).toHaveLength(300)
  })
})

class FakeWorker implements WorkerLike {
  onmessage: ((event: { data: unknown }) => void) | null = null
  onerror: ((event: unknown) => void) | null = null
  sent: Record<string, unknown>[] = []
  terminated = false
  reply: ((message: Record<string, unknown>, worker: FakeWorker) => void) | null = null

  postMessage(message: unknown) {
    this.sent.push(message as Record<string, unknown>)
    this.reply?.(message as Record<string, unknown>, this)
  }

  terminate() { this.terminated = true }
  emit(data: unknown) { this.onmessage?.({ data }) }
}

describe('the runner', () => {
  it('loads scripts and returns checked operations', async () => {
    const worker = new FakeWorker()
    worker.reply = (m, w) => {
      if (m.type === 'load') w.emit({ type: 'loaded', errors: [{ name: 'Bad', message: 'oops' }] })
      else w.emit({ type: 'result', id: m.id, ops: [{ op: 'set_value', field: 'status', value: 'Open' }, { op: 'hack' }] })
    }
    const runner = new ClientScriptRunner(meta, () => worker)

    expect(await runner.load([{ name: 'CS-1', title: 'Bad', source: 'x' }])).toEqual(['Bad: oops'])
    expect(await runner.run('refresh', { status: '' })).toEqual([{ op: 'set_value', field: 'status', value: 'Open' }])
    expect(worker.sent[1]).toMatchObject({ type: 'event', event: 'refresh', doc: { status: '' } })
  })

  it('ends a script that never answers and keeps the form usable', async () => {
    vi.useFakeTimers()
    const worker = new FakeWorker() // never replies
    const runner = new ClientScriptRunner(meta, () => worker, 50)

    const pending = runner.run('refresh', {})
    await vi.advanceTimersByTimeAsync(60)

    expect(await pending).toEqual([])
    expect(worker.terminated).toBe(true)
    expect(runner.stopped).toContain('took too long')
    expect(await runner.run('refresh', {})).toEqual([]) // off for the rest of the form's life
    vi.useRealTimers()
  })

  it('ignores answers it did not ask for and a worker that fails to start', async () => {
    const worker = new FakeWorker()
    const runner = new ClientScriptRunner(meta, () => worker)
    worker.emit({ type: 'result', id: 999, ops: [{ op: 'set_intro', text: 'x' }] })
    worker.emit('garbage')
    worker.emit(null)
    const loading = runner.load([])
    worker.onerror?.({})

    expect((await loading)[0]).toContain('could not start')
    expect(worker.terminated).toBe(true)
  })
})

describe('the document scripts see', () => {
  it('has typed values and leaves out passwords, tables and layout', () => {
    const m = { fields: [field('status'), field('amount', 'Float'), field('qty', 'Int'), field('done', 'Check'), field('due', 'Date'), field('pin', 'Password'), field('lines', 'Table'), field('sec', 'SectionBreak')] }
    const doc = scriptDoc(m, { status: 'Open', amount: '12.5', qty: '', done: true, due: '', pin: 'secret', lines: 'x' })

    expect(doc).toEqual({ status: 'Open', amount: 12.5, qty: null, done: true, due: null })
    expect(scriptDoc(m, { amount: 'abc' })).toEqual({ amount: null })
  })
})
