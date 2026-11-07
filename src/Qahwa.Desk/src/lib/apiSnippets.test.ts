import { describe, expect, it } from 'vitest'
import { apiExamples, exampleBody, sampleValue } from './apiSnippets.ts'

const def = {
  name: 'Sales Order',
  fields: [
    { name: 'customer', type: 'Data' as const, required: true },
    { name: 'total', type: 'Currency' as const, required: true },
    { name: 'section', type: 'SectionBreak' as const },
    { name: 'note', type: 'Text' as const },
  ],
}

describe('apiSnippets', () => {
  it('uses the required fields for the body and skips layout fields', () => {
    expect(exampleBody(def)).toEqual({ customer: 'Example', total: 10.5 })
    expect(Object.keys(exampleBody({ fields: [def.fields[2]!, def.fields[3]!] }))).toEqual(['note'])
  })
  it('gives a value for a select from its first option', () => {
    expect(sampleValue('Select', ['Open', 'Closed'])).toBe('Open')
  })
  it('builds encoded URLs and a submit call only for submittable types', () => {
    const list = apiExamples(def, 'http://x')
    expect(list[0]!.curl).toContain('http://x/api/resource/Sales%20Order?')
    expect(list.map((e) => e.title)).not.toContain('Submit')
    expect(apiExamples({ ...def, isSubmittable: true }, 'http://x').map((e) => e.title)).toContain('Submit')
  })
  it('offers only read and update for a single', () => {
    expect(apiExamples({ ...def, isSingle: true }, 'http://x').map((e) => e.title)).toEqual(['Read one', 'Update'])
  })
})
