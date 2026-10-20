import { describe, expect, it } from 'vitest'
import { isTyping, newDocumentPath, shortcutFor } from './shortcuts.ts'

const key = (k: string, over: Partial<{ ctrlKey: boolean; metaKey: boolean; altKey: boolean; shiftKey: boolean }> = {}) =>
  ({ key: k, ctrlKey: false, metaKey: false, altKey: false, shiftKey: false, ...over })

describe('shortcutFor', () => {
  it('knows the shortcuts', () => {
    expect(shortcutFor(key('k', { ctrlKey: true }), false)).toBe('search')
    expect(shortcutFor(key('K', { metaKey: true }), false)).toBe('search')
    expect(shortcutFor(key('n', { altKey: true }), false)).toBe('new')
    expect(shortcutFor(key('?', { shiftKey: true }), false)).toBe('help')
  })
  it('never interrupts typing, except search', () => {
    expect(shortcutFor(key('k', { ctrlKey: true }), true)).toBe('search')
    expect(shortcutFor(key('n', { altKey: true }), true)).toBeNull()
    expect(shortcutFor(key('?'), true)).toBeNull()
  })
  it('ignores other keys and combinations', () => {
    expect(shortcutFor(key('n'), false)).toBeNull()
    expect(shortcutFor(key('n', { altKey: true, ctrlKey: true }), false)).toBeNull()
    expect(shortcutFor(key('s', { ctrlKey: true }), false)).toBeNull()
  })
})

describe('isTyping', () => {
  it('is true for fields', () => {
    expect(isTyping({ tagName: 'INPUT' } as unknown as EventTarget)).toBe(true)
    expect(isTyping({ tagName: 'DIV', isContentEditable: true } as unknown as EventTarget)).toBe(true)
    expect(isTyping({ tagName: 'DIV' } as unknown as EventTarget)).toBe(false)
    expect(isTyping(null)).toBe(false)
  })
})

describe('newDocumentPath', () => {
  it('goes from a list or a document to the new form', () => {
    expect(newDocumentPath('/app/Ticket')).toBe('/app/Ticket/new')
    expect(newDocumentPath('/app/Sales%20Order/SO-1')).toBe('/app/Sales%20Order/new')
  })
  it('does nothing elsewhere', () => {
    expect(newDocumentPath('/app/Ticket/new')).toBeNull()
    expect(newDocumentPath('/')).toBeNull()
    expect(newDocumentPath('/designer')).toBeNull()
    expect(newDocumentPath('/app/Ticket/x/y')).toBeNull()
  })
})
