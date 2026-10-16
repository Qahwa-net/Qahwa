import { describe, expect, it } from 'vitest'
import { formatDuration, isHexColor, isSignature, mapUrl, parseDuration, parsePlace, placeLabel, placeText, starsLabel } from './fieldtypes.ts'

describe('duration', () => {
  it('formats the two largest units', () => {
    expect(formatDuration(0)).toBe('0s')
    expect(formatDuration(45)).toBe('45s')
    expect(formatDuration(5400)).toBe('1h 30m')
    expect(formatDuration(93600)).toBe('1d 2h')
    expect(formatDuration(-5)).toBe('0s')
    expect(formatDuration(Number.NaN)).toBe('0s')
  })

  it('understands what people type', () => {
    expect(parseDuration('90')).toBe(90)
    expect(parseDuration('1h 30m')).toBe(5400)
    expect(parseDuration('1H30M')).toBe(5400)
    expect(parseDuration('2d')).toBe(172800)
    expect(parseDuration('1:30')).toBe(5400)
    expect(parseDuration('1:30:15')).toBe(5415)
    expect(parseDuration(' 45 m ')).toBe(2700)
  })

  it('refuses what it cannot understand or what is too long', () => {
    for (const bad of ['', 'abc', '1x', '-5', '1.5h', '1h abc', '1::', '99999999999999999999', '4000d']) expect(parseDuration(bad)).toBeNull()
  })
})

describe('places and colours', () => {
  it('reads and writes places', () => {
    expect(parsePlace('{"lat":24.7,"lng":46.7}')).toEqual({ lat: 24.7, lng: 46.7 })
    expect(parsePlace('{"lat":91,"lng":0}')).toBeNull()
    expect(parsePlace('{"lat":"24","lng":46}')).toBeNull()
    expect(parsePlace('nonsense')).toBeNull()
    expect(parsePlace(null)).toBeNull()
    expect(placeText({ lat: 24.71360012345, lng: 46.6753 })).toBe('{"lat":24.7136,"lng":46.6753}')
    expect(placeLabel('{"lat":24.7,"lng":46.7}')).toBe('24.7, 46.7')
    expect(placeLabel('x')).toBe('')
  })

  it('makes a map address from numbers only', () => {
    expect(mapUrl({ lat: 24.7, lng: 46.7 })).toBe('https://www.openstreetmap.org/?mlat=24.7&mlon=46.7#map=16/24.7/46.7')
  })

  it('checks colours', () => {
    expect(isHexColor('#a1B2c3')).toBe(true)
    for (const bad of ['red', '#fff', '#12345g', '#1234567', 'url(x)']) expect(isHexColor(bad)).toBe(false)
  })
})

describe('stars and signatures', () => {
  it('draws stars, keeping within 0 to 5', () => {
    expect(starsLabel(3)).toBe('★★★☆☆')
    expect(starsLabel(0)).toBe('☆☆☆☆☆')
    expect(starsLabel(9)).toBe('★★★★★')
    expect(starsLabel('x')).toBe('☆☆☆☆☆')
  })

  it('only trusts a PNG data address as a picture', () => {
    expect(isSignature('data:image/png;base64,AAAA')).toBe(true)
    expect(isSignature('javascript:alert(1)')).toBe(false)
    expect(isSignature('data:image/svg+xml;base64,AAAA')).toBe(false)
    expect(isSignature(null)).toBe(false)
  })
})
