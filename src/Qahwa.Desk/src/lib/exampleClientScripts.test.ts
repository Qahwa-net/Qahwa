import { describe, expect, it } from 'vitest'
import workerSource from '../../public/client-script-worker.js?raw'
import projectScript from '../../../../apps/Qahwa.ProjectManagement/Scripts/project_form.client.js?raw'
import taskScript from '../../../../apps/Qahwa.ProjectManagement/Scripts/task_form.client.js?raw'

type Op = { op: string; [key: string]: unknown }

/** Runs the real sandbox worker (public/client-script-worker.js) in-process, as the form does, and returns what a script asks the form to do. */
function sandbox(doctype: string, source: string) {
  const listeners: Record<string, (m: { data: unknown }) => void> = {}
  const sent: Array<Record<string, unknown>> = []
  const fakeSelf = {
    postMessage: (m: Record<string, unknown>) => { sent.push(m) },
    addEventListener: (type: string, fn: (m: { data: unknown }) => void) => { listeners[type] = fn },
  }
  new Function('self', workerSource)(fakeSelf)
  listeners.message!({ data: { type: 'load', doctype, scripts: [{ name: 'example', source }] } })
  expect(sent.at(-1)).toMatchObject({ type: 'loaded', errors: [] })
  let id = 0
  const ask = (m: Record<string, unknown>): Op[] => {
    listeners.message!({ data: { ...m, id: ++id } })
    return (sent.at(-1) as { ops: Op[] }).ops
  }
  return {
    event: (event: string, doc: Record<string, unknown>) => ask({ type: 'event', event, doc }),
    button: (button: string, doc: Record<string, unknown>) => ask({ type: 'button', button, doc }),
  }
}

describe('example client script: Pm Project form', () => {
  const run = (event: string, doc: Record<string, unknown>) => sandbox('Pm Project', projectScript).event(event, doc)

  it('shows progress and hours', () => {
    expect(run('refresh', { percent_complete: 40, logged_hours: 12, budget_hours: 100 })).toContainEqual({ op: 'set_intro', text: '40% complete · 12 of 100 hours used.' })
  })
  it('warns when the budget is exceeded', () => {
    const ops = run('refresh', { percent_complete: 40, logged_hours: 130, budget_hours: 100 })
    expect(ops).toContainEqual({ op: 'set_intro', text: 'Over budget: 130 of 100 hours used.' })
  })
  it('makes the end date required while the project is active', () => {
    expect(run('status', { status: 'Active' })).toContainEqual({ op: 'set_df_property', field: 'end_date', property: 'reqd', value: true })
    expect(run('status', { status: 'Planning' })).toContainEqual({ op: 'set_df_property', field: 'end_date', property: 'reqd', value: false })
  })
  it('refuses to complete a project with open tasks', () => {
    expect(run('validate', { status: 'Completed', percent_complete: 60 })).toContainEqual(expect.objectContaining({ op: 'throw' }))
    expect(run('validate', { status: 'Completed', percent_complete: 100 })).toEqual([])
  })
})

describe('example client script: Pm Task form', () => {
  const run = (event: string, doc: Record<string, unknown>) => sandbox('Pm Task', taskScript).event(event, doc)

  it('says when a task is overdue and requires a person while it is in progress', () => {
    const ops = run('refresh', { status: 'In Progress', due_date: '2020-01-01' })
    expect(ops).toContainEqual({ op: 'set_intro', text: 'This task is overdue (due 2020-01-01).' })
    expect(ops).toContainEqual({ op: 'set_df_property', field: 'assigned_to', property: 'reqd', value: true })
  })
  it('does not call a finished task overdue and offers no button for it', () => {
    const ops = run('refresh', { status: 'Done', due_date: '2020-01-01' })
    expect(ops.find((o) => o.op === 'set_intro')).toBeUndefined()
    expect(ops.find((o) => o.op === 'button')).toBeUndefined()
  })
  it('has a Mark as done button that sets the status', () => {
    const s = sandbox('Pm Task', taskScript)
    const button = s.event('refresh', { status: 'Open' }).find((o) => o.op === 'button')!
    expect(button.label).toBe('Mark as done')
    expect(s.button(String(button.id), { status: 'Open' })).toContainEqual({ op: 'set_value', field: 'status', value: 'Done' })
  })
  it('refuses to save a task in progress without a person', () => {
    expect(run('validate', { status: 'In Progress', assigned_to: '' })).toContainEqual(expect.objectContaining({ op: 'throw' }))
    expect(run('validate', { status: 'In Progress', assigned_to: 'ann@example.com' })).toEqual([])
  })
})
