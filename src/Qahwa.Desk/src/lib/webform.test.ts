import { describe, expect, it } from 'vitest'
import type { PublicFormField } from '../api/types.ts'
import { inputType, isMultiline, missing, toPayload } from './webform.ts'

const f = (name: string, type: string, required = false): PublicFormField => ({ name, label: name, type, required, options: [], description: null })

describe('web form helpers', () => {
  const fields = [f('who', 'Data', true), f('age', 'Int'), f('agree', 'Check'), f('note', 'Text'), f('amount', 'Currency')]

  it('builds the payload with numbers, booleans and no empty answers', () => {
    expect(toPayload(fields, { who: ' Sam ', age: '41', agree: true, note: '   ', amount: 'abc' })).toEqual({ who: 'Sam', age: 41, agree: true, amount: 'abc' })
    expect(toPayload(fields, {})).toEqual({ agree: false })
  })

  it('never sends anything the form did not ask for', () => {
    expect(Object.keys(toPayload(fields, { who: 'x', owner: 'Administrator', docstatus: '1' } as never))).toEqual(['who', 'agree'])
  })

  it('lists required fields that are empty', () => {
    expect(missing(fields, { who: '  ' })).toEqual(['who'])
    expect(missing(fields, { who: 'Sam' })).toEqual([])
  })

  it('picks the input type', () => {
    expect(inputType('Email')).toBe('email')
    expect(inputType('Currency')).toBe('number')
    expect(inputType('Data')).toBe('text')
    expect(isMultiline('LongText')).toBe(true)
    expect(isMultiline('Data')).toBe(false)
  })
})
