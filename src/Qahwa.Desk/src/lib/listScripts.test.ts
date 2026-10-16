import { describe, expect, it } from 'vitest'
import workerSource from '../../public/client-script-worker.js?raw'
import type { DocField, DocTypeMeta } from '../api/types.ts'
import { sanitizeOps } from './clientScripts.ts'

const meta = { name: 'Order', fields: [{ name: 'status', type: 'Select' } as unknown as DocField, { name: 'pin', type: 'Password' } as unknown as DocField] } as unknown as DocTypeMeta

describe('list script operations', () => {
  it('keeps row colours and filters that make sense and drops the rest', () => {
    const ops = sanitizeOps([
      { op: 'set_row_indicator', name: 'SO-1', color: 'red' },
      { op: 'set_row_indicator', name: 'SO-2', color: 'hotpink' },
      { op: 'set_row_indicator', name: 5, color: 'red' },
      { op: 'set_filter', field: 'status', value: 'Open' },
      { op: 'set_filter', field: 'ghost', value: 'x' },
      { op: 'set_filter', field: 'pin', value: 'x' },
      { op: 'set_filter', field: 'status', value: { a: 1 } },
    ], meta)

    expect(ops).toEqual([{ op: 'set_row_indicator', name: 'SO-1', color: 'red' }, { op: 'set_filter', field: 'status', value: 'Open' }])
  })
})

/** Runs the real sandbox file against a stand-in for the worker's global scope. */
function startWorker() {
  const posted: Record<string, unknown>[] = []
  let handler: ((m: { data: unknown }) => void) | null = null
  const scope: Record<string, unknown> = {
    postMessage: (m: Record<string, unknown>) => posted.push(m),
    addEventListener: (type: string, fn: (m: { data: unknown }) => void) => { if (type === 'message') handler = fn },
  }
  new Function('self', workerSource)(scope)
  return { posted, send: (data: unknown) => handler!({ data }) }
}

describe('the sandbox for list scripts', () => {
  const script = `
    qahwa.ui.list.on('Order', {
      onload: function (list) { list.add_custom_button('Mark open', function (l) { l.msgprint('Marked ' + l.selected.length); l.set_filter('status', 'Open') }); list.set_intro('Open orders first') },
      refresh: function (list) { list.rows.forEach(function (r) { if (r.total > 100) list.set_row_indicator(r.name, 'red') }) },
    })`

  it('runs onload and refresh with copies of the rows and returns only operations', () => {
    const w = startWorker()
    w.send({ type: 'load', doctype: 'Order', scripts: [{ name: 's', source: script }] })
    w.send({ type: 'list_event', event: 'onload', rows: [], selected: [], id: 1 })
    w.send({ type: 'list_event', event: 'refresh', rows: [{ name: 'SO-1', total: 500 }, { name: 'SO-2', total: 5 }], selected: [], id: 2 })

    const results = w.posted.filter((m) => m.type === 'result')
    expect((results[0]!.ops as { op: string }[]).map((o) => o.op)).toEqual(['button', 'set_intro'])
    expect(results[1]!.ops).toEqual([{ op: 'set_row_indicator', name: 'SO-1', color: 'red' }])
  })

  it('runs a button with the ticked names, and an unknown button does nothing', () => {
    const w = startWorker()
    w.send({ type: 'load', doctype: 'Order', scripts: [{ name: 's', source: script }] })
    w.send({ type: 'list_event', event: 'onload', rows: [], selected: [], id: 1 })
    const id = ((w.posted.find((m) => m.type === 'result')!.ops as { id: string }[])[0]!).id
    w.send({ type: 'list_button', button: id, rows: [], selected: ['SO-1', 'SO-2'], id: 2 })
    w.send({ type: 'list_button', button: 'nope', rows: [], selected: [], id: 3 })

    const results = w.posted.filter((m) => m.type === 'result')
    expect(results[1]!.ops).toEqual([{ op: 'msgprint', text: 'Marked 2' }, { op: 'set_filter', field: 'status', value: 'Open' }])
    expect(results[2]!.ops).toEqual([])
  })

  it('reports a script that throws instead of breaking the list, and ignores other doctypes', () => {
    const w = startWorker()
    w.send({ type: 'load', doctype: 'Order', scripts: [{ name: 's', source: "qahwa.ui.list.on('Order', { refresh: function () { throw new Error('boom') } }); qahwa.ui.list.on('Other', { refresh: function (l) { l.set_intro('no') } })" }] })
    w.send({ type: 'list_event', event: 'refresh', rows: [], selected: [], id: 1 })

    expect(w.posted.find((m) => m.type === 'result')!.ops).toEqual([{ op: 'error', message: 'boom' }])
  })
})
