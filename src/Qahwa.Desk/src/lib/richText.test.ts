// @vitest-environment jsdom
import { describe, expect, it } from 'vitest'
import { htmlToText, safeHref, sanitizeHtml } from './richText.ts'

describe('rich text', () => {
  it('keeps simple formatting and drops everything else', () => {
    expect(sanitizeHtml('<p onclick="x()">Hi <b>there</b></p>')).toBe('<p>Hi <b>there</b></p>')
    expect(sanitizeHtml('<script>alert(1)</script>ok')).toBe('ok')
    expect(sanitizeHtml('<img src=x onerror=alert(1)>t')).toBe('t')
    expect(sanitizeHtml('<style>p{display:none}</style>t')).toBe('t')
    expect(sanitizeHtml('<iframe src="//evil"></iframe>t')).toBe('t')
  })

  it('checks link addresses', () => {
    expect(sanitizeHtml('<a href="https://e.com/?a=1&b=2">l</a>')).toContain('href="https://e.com/?a=1&amp;b=2"')
    expect(sanitizeHtml('<a href="javascript:alert(1)">l</a>')).toBe('<a>l</a>')
    expect(sanitizeHtml('<a href="&#106;avascript:alert(1)">l</a>')).toBe('<a>l</a>')
    expect(sanitizeHtml('<a href="java\tscript:alert(1)">l</a>')).toBe('<a>l</a>')
    expect(sanitizeHtml('<a href="data:text/html,x">l</a>')).toBe('<a>l</a>')
    expect(safeHref('  mailto:a@b.c ')).toBe('mailto:a@b.c')
    expect(safeHref('/relative')).toBeNull()
  })

  it('shows plain text for lists', () => {
    expect(htmlToText('<p>one</p><p>two &amp; three</p>')).toBe('one two & three')
    expect(htmlToText('<script>x</script>a<br>b')).toBe('a b')
    expect(htmlToText(null)).toBe('')
  })
})
