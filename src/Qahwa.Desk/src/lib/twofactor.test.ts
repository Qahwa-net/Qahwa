import { describe, expect, it } from 'vitest'
import { cleanCode, groupKey, qrImage, qrSvg, recoveryFile } from './twofactor.ts'

describe('two-factor helpers', () => {
  it('groups the setup key and cleans typed codes', () => {
    expect(groupKey('ABCDEFGHIJ')).toBe('ABCD EFGH IJ')
    expect(cleanCode('12 34-56789')).toBe('123456')
    expect(cleanCode('<b>')).toBe('')
  })

  it('writes the recovery file', () => {
    const text = recoveryFile('a@example.com', ['AAAAA-BBBBB', 'CCCCC-DDDDD'])
    expect(text).toContain('a@example.com')
    expect(text).toContain('AAAAA-BBBBB\nCCCCC-DDDDD')
  })

  it('makes a QR code as an svg image, not as markup to inject', () => {
    const uri = 'otpauth://totp/Qahwa:a%40b.com?secret=ABCDEFGH&issuer=Qahwa'
    expect(qrSvg(uri)).toContain('<svg')
    expect(qrImage(uri).startsWith('data:image/svg+xml;charset=utf-8,')).toBe(true)
    expect(qrImage(uri)).not.toContain('<')
  })
})
