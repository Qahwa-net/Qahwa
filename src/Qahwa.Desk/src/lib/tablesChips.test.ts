import { describe, expect, it } from 'vitest'
import type { DocField, DocTypeMeta } from '../api/types.ts'
import { addChip, chipField, chipValues, removeChip, type TableRow } from './tables.ts'

const field = (name: string, type = 'Data', extra: Partial<DocField> = {}): DocField => ({ name, type, label: name, required: false, unique: false, ...extra } as unknown as DocField)
const child = (...fields: DocField[]) => ({ name: 'Row', fields } as unknown as DocTypeMeta)
const row = (skill: string, key = skill): TableRow => ({ key, values: { skill } })

describe('multi-select tables', () => {
  it('finds the one link field, and nothing when the child does not fit', () => {
    expect(chipField(child(field('skill', 'Link', { options: 'Skill', required: true })))?.name).toBe('skill')
    expect(chipField(child(field('skill', 'Link'), field('note')))?.name).toBe('skill')
    expect(chipField(child(field('a', 'Link'), field('b', 'Link')))).toBeNull()
    expect(chipField(child(field('a', 'Link'), field('note', 'Data', { required: true })))).toBeNull()
    expect(chipField(child(field('note')))).toBeNull()
  })

  it('lists, adds (once) and removes the chosen documents', () => {
    const skill = field('skill', 'Link', { options: 'Skill' })
    const c = child(skill)
    const rows = [row('C#'), row('SQL')]

    expect(chipValues(rows, skill)).toEqual(['C#', 'SQL'])
    expect(addChip(rows, c, skill, 'C#')).toBe(rows)
    const added = addChip(rows, c, skill, 'Go')
    expect(chipValues(added, skill)).toEqual(['C#', 'SQL', 'Go'])
    expect(chipValues(removeChip(added, skill, 'SQL'), skill)).toEqual(['C#', 'Go'])
    expect(chipValues([{ key: 'x', values: { skill: '' } }], skill)).toEqual([])
  })
})
