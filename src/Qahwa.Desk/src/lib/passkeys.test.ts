import { describe, expect, it } from 'vitest'
import { fromBase64Url, passkeyErrorText, toBase64Url, toCreationOptions, toRequestOptions } from './passkeys.ts'

describe('base64url', () => {
  it('round-trips bytes, with no padding and URL-safe characters', () => {
    const bytes = new Uint8Array([251, 255, 254, 0, 1, 2, 3])
    const text = toBase64Url(bytes)
    expect(text).toBe('-__-AAECAw')
    expect([...fromBase64Url(text)]).toEqual([...bytes])
  })
  it('handles the empty value', () => expect(toBase64Url(new Uint8Array())).toBe(''))
})

describe('options', () => {
  it('turns challenge, user id and credential ids into bytes', () => {
    const options = toCreationOptions({ challenge: 'AQID', user: { id: 'BAUG' }, excludeCredentials: [{ id: 'BwgJ' }] })
    expect([...new Uint8Array(options.challenge as ArrayBuffer)]).toEqual([1, 2, 3])
    expect([...new Uint8Array(options.user!.id as ArrayBuffer)]).toEqual([4, 5, 6])
    expect([...new Uint8Array(options.excludeCredentials![0]!.id as ArrayBuffer)]).toEqual([7, 8, 9])
  })
  it('keeps an empty allow list (any passkey for the site) empty', () => {
    expect(toRequestOptions({ challenge: 'AQID', allowCredentials: [] }).allowCredentials).toEqual([])
  })
})

describe('passkeyErrorText', () => {
  it('stays quiet when the person cancelled', () => expect(passkeyErrorText(new DOMException('x', 'NotAllowedError'))).toBeNull())
  it('explains a duplicate and any other failure', () => {
    expect(passkeyErrorText(new DOMException('x', 'InvalidStateError'))).toMatch(/already/)
    expect(passkeyErrorText(new Error('boom'))).toMatch(/password/)
  })
})
