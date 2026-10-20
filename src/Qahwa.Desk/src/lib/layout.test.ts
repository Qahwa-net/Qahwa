import { describe, expect, it } from 'vitest'
import type { DocField } from '../api/types.ts'
import { buildLayout } from './layout.ts'

const f = (name: string, type: DocField['type'] = 'Data', label?: string) => ({ name, type, label: label ?? name }) as DocField

describe('buildLayout', () => {
  it('puts loose fields in one untitled section of one tab', () => {
    const tabs = buildLayout([f('a'), f('b')])
    expect(tabs).toHaveLength(1)
    expect(tabs[0]!.sections.map((s) => [s.label, s.fields.map((x) => x.name)])).toEqual([['', ['a', 'b']]])
  })

  it('starts a titled section at each section break and keeps every field of its columns', () => {
    const tabs = buildLayout([f('a'), f('s1', 'SectionBreak', 'Contact'), f('b'), f('c1', 'ColumnBreak'), f('c'), f('s2', 'SectionBreak', 'More'), f('d')])
    expect(tabs[0]!.sections.map((s) => [s.label, s.fields.map((x) => x.name)])).toEqual([['', ['a']], ['Contact', ['b', 'c']], ['More', ['d']]])
  })

  it('splits a section into columns at column breaks, at most four, never empty', () => {
    const tabs = buildLayout([f('c0', 'ColumnBreak'), f('a'), f('c1', 'ColumnBreak'), f('c2', 'ColumnBreak'), f('b'), f('c3', 'ColumnBreak'), f('c'), f('c4', 'ColumnBreak'), f('d'), f('c5', 'ColumnBreak'), f('e'), f('c6', 'ColumnBreak')])
    expect(tabs[0]!.sections[0]!.columns.map((c) => c.map((x) => x.name))).toEqual([['a'], ['b'], ['c'], ['d', 'e']])
    expect(tabs[0]!.sections[0]!.fields.map((x) => x.name)).toEqual(['a', 'b', 'c', 'd', 'e'])
    expect(buildLayout([f('a'), f('b')])[0]!.sections[0]!.columns).toHaveLength(1)
  })

  it('starts a tab at each tab break and names the first one', () => {
    const tabs = buildLayout([f('a'), f('t', 'TabBreak', 'Billing'), f('b')], () => true, 'Main')
    expect(tabs.map((t) => t.label)).toEqual(['Main', 'Billing'])
  })

  it('drops hidden fields, empty sections and empty tabs, and a hidden break starts nothing', () => {
    const hidden = new Set(['x', 's2'])
    const tabs = buildLayout(
      [f('s1', 'SectionBreak', 'Empty'), f('x'), f('t', 'TabBreak', 'Gone'), f('s2', 'SectionBreak', 'Hidden break'), f('a')],
      (field) => !hidden.has(field.name),
    )
    expect(tabs.map((t) => t.label)).toEqual(['Gone'])
    expect(tabs[0]!.sections.map((s) => [s.label, s.fields.map((x) => x.name)])).toEqual([['', ['a']]])
  })

  it('returns no tabs when nothing is shown', () => {
    expect(buildLayout([f('a')], () => false)).toEqual([])
  })
})
