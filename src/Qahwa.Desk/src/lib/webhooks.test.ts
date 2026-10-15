import { describe, expect, it } from 'vitest'
import { newHook, randomSecret, toSet, urlHint } from './webhooks.ts'

describe('webhook helpers', () => {
  it('names new hooks without clashing', () => {
    expect(newHook([]).name).toBe('Webhook 1')
    expect(newHook([{ ...newHook([]), name: 'Webhook 2' }]).name).toBe('Webhook 3')
  })

  it('makes a long random secret', () => {
    const secret = randomSecret(() => new Uint8Array(32).fill(255))
    expect(secret).toBe('ff'.repeat(32))
    expect(randomSecret()).toHaveLength(64)
    expect(randomSecret()).not.toBe(randomSecret())
  })

  it('sends a blank secret to keep the stored one and drops blank conditions', () => {
    const out = toSet({ docType: 'T', hooks: [{ ...newHook([]), name: ' A ', url: ' https://x.example.com ', condition: '  ', secret: undefined, secretSet: true }] }).hooks[0]!
    expect(out).toMatchObject({ name: 'A', url: 'https://x.example.com', condition: null, secret: '' })
  })

  it('warns about addresses the server would refuse', () => {
    expect(urlHint('https://hooks.example.com/x')).toBeNull()
    expect(urlHint('https://')).toBeNull()
    expect(urlHint('http://hooks.example.com')).toMatch(/https/)
    expect(urlHint('https://u:p@hooks.example.com')).toMatch(/user name/)
    expect(urlHint('https://localhost/x')).toMatch(/private/i)
    expect(urlHint('https://192.168.1.5/x')).toMatch(/private/i)
  })
})
