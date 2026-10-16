import qrcode from 'qrcode-generator'

/** The setup key in groups of four so it is easy to type: "ABCD EFGH ...". */
export const groupKey = (secret: string): string => secret.replace(/(.{4})/g, '$1 ').trim()

/** The text of the recovery codes file people are asked to keep. */
export function recoveryFile(account: string, codes: string[]): string {
  return [`Qahwa recovery codes for ${account}`, 'Each code works once. Keep this file somewhere safe.', '', ...codes, ''].join('\n')
}

/** The setup address as a QR code (made here in the browser; the secret never goes to another site). */
export function qrSvg(text: string): string {
  const qr = qrcode(0, 'M')
  qr.addData(text)
  qr.make()
  return qr.createSvgTag({ cellSize: 4, margin: 2, scalable: true })
}

/** Digits only, at most six: what a person types while reading the code from their phone. */
export const cleanCode = (text: string): string => text.replace(/\D/g, '').slice(0, 6)

/** The QR code as an image address. Shown with an img tag, so nothing in it can ever run as script. */
export const qrImage = (text: string): string => `data:image/svg+xml;charset=utf-8,${encodeURIComponent(qrSvg(text))}`
