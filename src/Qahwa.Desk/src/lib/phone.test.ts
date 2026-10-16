import { describe, expect, it } from 'vitest'
import { dialCountries, joinPhone, splitPhone } from './phone.ts'

const countries = dialCountries([
  { name: 'Saudi Arabia', dial_code: '966' }, { name: 'United States', dial_code: '1' }, { name: 'Jamaica', dial_code: '1876' },
  { name: 'Nowhere', dial_code: '' }, { name: 'Russia', dial_code: '7' }, { name: '', dial_code: '5' },
])

describe('phone numbers with a country', () => {
  it('keeps only countries that have a dialling code, sorted by name', () => {
    expect(countries.map((c) => c.name)).toEqual(['Jamaica', 'Russia', 'Saudi Arabia', 'United States'])
  })

  it('splits a stored number at the longest matching code', () => {
    expect(splitPhone('+966 50 123 4567', countries)).toEqual({ country: { name: 'Saudi Arabia', dial: '966' }, rest: '50 123 4567' })
    expect(splitPhone('+18765550100', countries).country?.name).toBe('Jamaica')
    expect(splitPhone('+1 202 555 0100', countries).country?.name).toBe('United States')
  })

  it('leaves numbers without a "+" or with an unknown code as they are', () => {
    expect(splitPhone('0501234567', countries)).toEqual({ country: null, rest: '0501234567' })
    expect(splitPhone('+999 123', countries)).toEqual({ country: null, rest: '+999 123' })
    expect(splitPhone('', countries)).toEqual({ country: null, rest: '' })
  })

  it('joins a country and a number, and gives nothing for an empty number', () => {
    const saudi = countries.find((c) => c.name === 'Saudi Arabia')!
    expect(joinPhone(saudi, ' 50 123 4567 ')).toBe('+966 50 123 4567')
    expect(joinPhone(saudi, '  ')).toBe('')
    expect(joinPhone(null, '0501234567')).toBe('0501234567')
  })
})
