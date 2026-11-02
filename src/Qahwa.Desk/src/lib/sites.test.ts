import { describe, expect, it } from 'vitest'
import { parseHosts, validSiteName } from './sites.ts'

describe('parseHosts', () => {
  it('splits on lines, commas and spaces, lower-cases and drops duplicates and blanks', () => {
    expect(parseHosts('Acme.test, *.acme.test\n\nacme.test  shop.test;')).toEqual(['acme.test', '*.acme.test', 'shop.test'])
  })
  it('gives nothing for empty text', () => expect(parseHosts('  \n ')).toEqual([]))
})

describe('validSiteName', () => {
  it('accepts lower-case letters, digits and dashes', () => expect(validSiteName('acme-eu-2')).toBe(true))
  it('refuses capitals, spaces, a leading dash and long names', () => {
    for (const bad of ['Acme', 'a b', '-acme', '', 'a'.repeat(41)]) expect(validSiteName(bad)).toBe(false)
  })
})
