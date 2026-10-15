const ADDRESS = /^[^\s@<>,;"]+@[^\s@<>,;"]+\.[^\s@<>,;"]+$/

/** Splits typed text into addresses (commas, semicolons, spaces or line breaks) and says which are not addresses. */
export function parseAddresses(text: string): { valid: string[]; invalid: string[] } {
  const valid: string[] = []
  const invalid: string[] = []
  for (const part of text.split(/[\s,;]+/).map((p) => p.trim()).filter(Boolean)) {
    if (ADDRESS.test(part)) {
      if (!valid.some((v) => v.toLowerCase() === part.toLowerCase())) valid.push(part)
    } else {
      invalid.push(part)
    }
  }
  return { valid, invalid }
}

export const MAX_RECIPIENTS = 10
