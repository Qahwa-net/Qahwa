const ALLOWED = new Set(['p', 'br', 'div', 'span', 'b', 'strong', 'i', 'em', 'u', 's', 'sub', 'sup', 'ul', 'ol', 'li',
  'h1', 'h2', 'h3', 'h4', 'blockquote', 'pre', 'code', 'hr', 'a', 'table', 'thead', 'tbody', 'tr', 'th', 'td'])
const DROP_WHOLE = new Set(['script', 'style', 'iframe', 'object', 'noscript', 'template', 'textarea', 'title', 'svg', 'math', 'head', 'xmp', 'noembed', 'noframes'])
const SCHEMES = ['http://', 'https://', 'mailto:', 'tel:']

/** An address a link may point to: a web, mail or phone address (the server applies the same rule). */
export function safeHref(value: string | null | undefined): string | null {
  if (!value || value.length > 2000) return null
  const text = value.trim()
  const squeezed = [...text].filter((c) => c > ' ').join('').toLowerCase()
  return SCHEMES.some((s) => squeezed.startsWith(s)) ? text : null
}

/**
 * Formatted text made safe to show: a copy built from an inert document using only the same short list of tags the server keeps,
 * with links checked. The server cleans what it stores; this is a second line, for text that reached the database another way.
 */
export function sanitizeHtml(html: string | null | undefined): string {
  if (!html) return ''
  const source = new DOMParser().parseFromString(`<body>${html}`, 'text/html').body
  const target = document.implementation.createHTMLDocument('').body

  const copy = (from: Node, into: Node) => {
    for (const node of Array.from(from.childNodes)) {
      if (node.nodeType === Node.TEXT_NODE) {
        into.appendChild(target.ownerDocument.createTextNode(node.textContent ?? ''))
      } else if (node.nodeType === Node.ELEMENT_NODE) {
        const el = node as Element
        const name = el.tagName.toLowerCase()
        if (DROP_WHOLE.has(name)) continue
        if (!ALLOWED.has(name)) {
          copy(el, into) // the tag goes, its text stays
          continue
        }
        const clean = target.ownerDocument.createElement(name)
        if (name === 'a') {
          const href = safeHref(el.getAttribute('href'))
          if (href) {
            clean.setAttribute('href', href)
            clean.setAttribute('target', '_blank')
            clean.setAttribute('rel', 'noopener noreferrer nofollow')
          }
        }
        copy(el, clean)
        into.appendChild(clean)
      }
    }
  }

  copy(source, target)
  return target.innerHTML
}

/** The text of some formatted text, for lists and previews (no document needed). */
export function htmlToText(html: string | null | undefined): string {
  if (!html) return ''
  return html
    .replace(/<(script|style)[^>]*>[\s\S]*?<\/\1\s*>/gi, ' ')
    .replace(/<\/?(br|p|div|li|tr|h[1-4]|blockquote|pre|hr)\b[^>]*>/gi, ' ')
    .replace(/<[^>]*>/g, '')
    .replace(/&nbsp;/g, ' ').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&amp;/g, '&')
    .replace(/\s+/g, ' ').trim()
}
