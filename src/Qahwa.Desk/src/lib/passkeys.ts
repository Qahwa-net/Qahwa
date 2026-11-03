/** What the server sends for a passkey ceremony: a signed token to send back, and the browser's options with text in place of bytes. */
export interface PasskeyCeremony {
  token: string
  publicKey: Record<string, unknown> & { challenge: string; user?: { id: string }; excludeCredentials?: { id: string }[]; allowCredentials?: { id: string }[] }
}

export function toBase64Url(bytes: ArrayBuffer | Uint8Array): string {
  const view = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes)
  let text = ''
  for (const b of view) text += String.fromCharCode(b)
  return btoa(text).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

export function fromBase64Url(text: string): Uint8Array<ArrayBuffer> {
  const padded = text.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(text.length / 4) * 4, '=')
  const raw = atob(padded)
  const bytes = new Uint8Array(new ArrayBuffer(raw.length))
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i)
  return bytes
}

/** True in a browser that can make and use passkeys. */
export const passkeysSupported = (): boolean => typeof window !== 'undefined' && !!window.PublicKeyCredential && !!navigator.credentials

/** Turns the server's options into what `navigator.credentials` wants (bytes in place of text). */
export function toCreationOptions(options: PasskeyCeremony['publicKey']): PublicKeyCredentialCreationOptions {
  return {
    ...options,
    challenge: fromBase64Url(options.challenge),
    user: { ...(options.user as object), id: fromBase64Url(options.user!.id) },
    excludeCredentials: (options.excludeCredentials ?? []).map((c) => ({ ...c, type: 'public-key' as const, id: fromBase64Url(c.id) })),
  } as unknown as PublicKeyCredentialCreationOptions
}

export function toRequestOptions(options: PasskeyCeremony['publicKey']): PublicKeyCredentialRequestOptions {
  return {
    ...options,
    challenge: fromBase64Url(options.challenge),
    allowCredentials: (options.allowCredentials ?? []).map((c) => ({ ...c, type: 'public-key' as const, id: fromBase64Url(c.id) })),
  } as unknown as PublicKeyCredentialRequestOptions
}

/** Asks the device to make a passkey; the result is what the server wants back. */
export async function createPasskey(ceremony: PasskeyCeremony, label: string) {
  const credential = (await navigator.credentials.create({ publicKey: toCreationOptions(ceremony.publicKey) })) as PublicKeyCredential | null
  if (!credential) throw new Error('cancelled')
  const response = credential.response as AuthenticatorAttestationResponse
  return { token: ceremony.token, id: toBase64Url(credential.rawId), clientDataJson: toBase64Url(response.clientDataJSON), attestationObject: toBase64Url(response.attestationObject), label }
}

/** Asks the device to prove a passkey; the result is what the server wants back. */
export async function proveWithPasskey(ceremony: PasskeyCeremony) {
  const credential = (await navigator.credentials.get({ publicKey: toRequestOptions(ceremony.publicKey) })) as PublicKeyCredential | null
  if (!credential) throw new Error('cancelled')
  const response = credential.response as AuthenticatorAssertionResponse
  return {
    token: ceremony.token,
    id: toBase64Url(credential.rawId),
    clientDataJson: toBase64Url(response.clientDataJSON),
    authenticatorData: toBase64Url(response.authenticatorData),
    signature: toBase64Url(response.signature),
    userHandle: response.userHandle ? toBase64Url(response.userHandle) : '',
  }
}

/** A person-readable reason for a failed ceremony (the browser's own cancel and timeout errors are not errors to shout about). */
export function passkeyErrorText(e: unknown): string | null {
  if (e instanceof DOMException && (e.name === 'NotAllowedError' || e.name === 'AbortError')) return null
  if (e instanceof DOMException && e.name === 'InvalidStateError') return 'This device already has a passkey for this account.'
  return 'The passkey could not be used. Try again, or sign in with your password.'
}
