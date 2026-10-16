import { describe, expect, it } from 'vitest'
import { liveAction, parseEvent, realtimeUrl } from './realtime.ts'

describe('live updates', () => {
  it('builds the address of the stream, escaping the document name', () => {
    expect(realtimeUrl()).toBe('/api/realtime')
    expect(realtimeUrl('Sales Order', 'SO/1 & 2')).toBe('/api/realtime?doctype=Sales%20Order&name=SO%2F1%20%26%202')
  })

  it('reads the messages it knows and ignores the rest without throwing', () => {
    expect(parseEvent('doc', '{"doctype":"A","name":"b","user":"x@y.z"}')).toEqual({ type: 'doc', doctype: 'A', name: 'b', user: 'x@y.z', deleted: false })
    expect(parseEvent('doc', '{"doctype":"A","name":"b","deleted":true}')?.deleted).toBe(true)
    expect(parseEvent('notification', '{}')).toEqual({ type: 'notification', doctype: undefined, name: undefined, user: undefined, deleted: false })
    expect(parseEvent('other', '{}')).toBeNull()
    expect(parseEvent('doc', 'not json')).toBeNull()
    expect(parseEvent('doc', 'null')).toBeNull()
    expect(parseEvent('doc', '{"name":5,"user":{"a":1}}')?.name).toBeUndefined()
  })

  it('refreshes a form with no edits, warns one with edits and never reacts to the person\'s own save', () => {
    const changed = { type: 'doc' as const, doctype: 'A', name: 'b', user: 'ann@x' }
    expect(liveAction(changed, 'bob@x', false)).toEqual({ kind: 'refresh', by: 'ann@x' })
    expect(liveAction(changed, 'bob@x', true)).toEqual({ kind: 'warn', by: 'ann@x' })
    expect(liveAction(changed, 'ann@x', false)).toEqual({ kind: 'ignore' })
    expect(liveAction({ ...changed, deleted: true }, 'bob@x', true)).toEqual({ kind: 'deleted', by: 'ann@x' })
    expect(liveAction({ type: 'notification' }, 'bob@x', false)).toEqual({ kind: 'ignore' })
  })
})
