import { describe, expect, it } from 'vitest'
import { isEditable } from './fields.ts'

describe('password fields', () => {
  it('are edited in the form like other fields', () => {
    expect(isEditable('Password')).toBe(true)
  })
})
