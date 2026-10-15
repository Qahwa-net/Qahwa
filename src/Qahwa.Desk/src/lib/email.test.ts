import { describe, expect, it } from 'vitest'
import { parseAddresses } from './email.ts'

describe('parseAddresses', () => {
  it('splits on commas, semicolons, spaces and lines and drops repeats', () => {
    expect(parseAddresses('a@x.com, b@y.org;c@z.io\nA@X.com').valid).toEqual(['a@x.com', 'b@y.org', 'c@z.io'])
  })

  it('reports what is not an address', () => {
    const { valid, invalid } = parseAddresses('a@x.com nobody <b@y.org> a@b')
    expect(valid).toEqual(['a@x.com'])
    expect(invalid).toEqual(['nobody', '<b@y.org>', 'a@b'])
  })

  it('is fine with nothing', () => {
    expect(parseAddresses('  ')).toEqual({ valid: [], invalid: [] })
  })
})
