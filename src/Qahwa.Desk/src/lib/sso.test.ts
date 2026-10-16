import { describe, expect, it } from 'vitest'
import { ssoErrorText, ssoStartUrl } from './sso.ts'

describe('single sign-on helpers', () => {
  it('turns codes into sentences and never echoes unknown text', () => {
    expect(ssoErrorText(null)).toBeNull()
    expect(ssoErrorText('disabled')).toContain('switched off')
    expect(ssoErrorText('<script>alert(1)</script>')).toBe('Single sign-on failed. Please try again or use your password.')
    expect(ssoErrorText('constructor')).toBe('Single sign-on failed. Please try again or use your password.')
  })

  it('builds the start address with an encoded name and a same-site next', () => {
    expect(ssoStartUrl('idp', '/app/Customer')).toBe('/api/auth/sso/idp/start?next=%2Fapp%2FCustomer')
    expect(ssoStartUrl('a b/c', null)).toBe('/api/auth/sso/a%20b%2Fc/start?next=%2F')
    expect(ssoStartUrl('idp', '//evil.example.com')).toContain('next=%2F')
    expect(ssoStartUrl('idp', 'https://evil.example.com')).toBe('/api/auth/sso/idp/start?next=%2F')
  })
})
