export interface SsoProviderInfo {
  name: string
  display_name: string
}

const MESSAGES: Record<string, string> = {
  unknown_provider: 'That sign-in provider is not available.',
  not_configured: 'Single sign-on is not set up on this server.',
  provider_refused: 'The sign-in was cancelled or refused by the provider.',
  disabled: 'This account is switched off. Ask an administrator.',
  no_account: 'There is no account for you here. Ask an administrator to add you.',
  domain_not_allowed: 'Your email address is not allowed to sign in here.',
  email_not_verified: 'Your provider has not confirmed your email address.',
  already_linked: 'This account is already linked to a different identity.',
  expired: 'The sign-in took too long or was started elsewhere. Please try again.',
  bad_state: 'The sign-in could not be checked. Please try again.',
  bad_signature: 'The answer from your company login could not be trusted.',
  unsigned: 'The answer from your company login was not signed.',
  bad_audience: 'Your company login answered for another application.',
  expired_assertion: 'The answer from your company login has expired. Please try again.',
  replayed: 'That sign-in was already used. Please start again.',
}

/** What to tell the person when the server sent them back with a short code; unknown codes get the general text. */
export function ssoErrorText(code: string | null): string | null {
  if (!code) return null
  return Object.prototype.hasOwnProperty.call(MESSAGES, code) ? MESSAGES[code]! : 'Single sign-on failed. Please try again or use your password.'
}

/** The same, for a SAML provider. */
export function samlLoginUrl(name: string, next: string | null): string {
  return ssoStartUrl(name, next).replace('/api/auth/sso/', '/api/auth/saml/').replace('/start?', '/login?')
}

/** Where the button goes. The name comes from the server's list and is encoded anyway; `next` stays a same-site path. */
export function ssoStartUrl(name: string, next: string | null): string {
  const safe = next && next.startsWith('/') && !next.startsWith('//') && !next.startsWith('/\\') ? next : '/'
  return `/api/auth/sso/${encodeURIComponent(name)}/start?next=${encodeURIComponent(safe)}`
}
