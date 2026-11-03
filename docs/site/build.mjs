// Builds the documentation (docs/**/*.md and the repository README) into a static website in docs/site/dist.
//   npm run build   writes the site        npm run check   only checks that every link between pages works
import { mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { dirname, join, relative, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import { marked, Renderer } from 'marked'

const here = dirname(fileURLToPath(import.meta.url))
const docs = resolve(here, '..')
const root = resolve(docs, '..')
const dist = join(here, 'dist')
const checkOnly = process.argv.includes('--check')

const walk = (dir) => readdirSync(dir).flatMap((name) => {
  const path = join(dir, name)
  if (name === 'site' || name === 'node_modules') return []
  return statSync(path).isDirectory() ? walk(path) : path.endsWith('.md') ? [path] : []
})

/** Every page: where it is read from and the page it becomes (paths relative to docs/, with / separators). */
const pages = walk(docs).map((file) => ({ file, rel: relative(docs, file).split(sep).join('/') }))
pages.push({ file: join(root, 'README.md'), rel: 'overview.md' })
const known = new Map(pages.map((p) => [p.rel, p]))
const htmlName = (rel) => rel.replace(/\.md$/, '.html')

const titleOf = (text, fallback) => /^#\s+(.+)$/m.exec(text)?.[1].replace(/[`*_]/g, '') ?? fallback
for (const page of pages) {
  page.text = readFileSync(page.file, 'utf8')
  page.title = titleOf(page.text, page.rel)
}

const group = (rel) =>
  rel === 'README.md' || rel === 'overview.md' ? 'Start'
  : rel.startsWith('manual/developer/') ? 'Developer manual'
  : rel.startsWith('manual/user/') ? 'User guide'
  : rel.startsWith('adr/') ? 'Architecture decisions'
  : 'Reference'
const order = ['Start', 'User guide', 'Developer manual', 'Reference', 'Architecture decisions']

const problems = []
const escape = (s) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c])

function render(page) {
  const renderer = new Renderer()
  const base = page.rel.includes('/') ? dirname(page.rel) : ''
  renderer.link = function ({ href, title, tokens }) {
    const text = this.parser.parseInline(tokens)
    let target = href ?? ''
    if (/^(https?:|mailto:|#)/.test(target)) return `<a href="${escape(target)}"${title ? ` title="${escape(title)}"` : ''}>${text}</a>`
    const [path, hash] = target.split('#')
    if (path) {
      // README.md is written for the repository root, the others for docs/: resolve each the way its author meant it.
      const from = page.rel === 'overview.md' ? '' : base
      const resolved = resolve('/', from, path).slice(1)
      // Some pages write repository-root paths ("docs/adr/…") even from inside docs/: accept those too.
      const fromRoot = path.replace(/^(\.\/)?docs\//, '')
      const candidate = page.rel === 'overview.md' && resolved === 'docs/README.md' ? 'README.md' : resolved.replace(/^docs\//, '')
      const asPage = known.has(candidate) ? candidate : known.has(fromRoot) ? fromRoot : candidate
      if (known.has(asPage)) target = relativeLink(page.rel, htmlName(asPage)) + (hash ? `#${hash}` : '')
      else if (/\.(md)$/.test(path)) problems.push(`${page.rel}: the link "${href}" points at a page that does not exist`)
      else target = `https://github.com/Qahwa-net/Qahwa/blob/master/${(page.rel === 'overview.md' ? '' : 'docs/') + (from ? from + '/' : '')}${path}`.replace(/\/\.\//g, '/')
    }
    return `<a href="${escape(target)}"${title ? ` title="${escape(title)}"` : ''}>${text}</a>`
  }
  return marked.parse(page.text, { renderer, gfm: true })
}

const relativeLink = (from, to) => {
  const up = from.split('/').length - 1
  return '../'.repeat(up) + to
}

const nav = (current) => order.map((name) => {
  const items = pages.filter((p) => group(p.rel) === name).sort((a, b) => a.rel.localeCompare(b.rel, 'en', { numeric: true }))
  if (items.length === 0) return ''
  const list = items.map((p) => `<li><a href="${relativeLink(current, htmlName(p.rel))}"${p.rel === current ? ' aria-current="page"' : ''}>${escape(p.title)}</a></li>`).join('')
  return `<section><h2>${name}</h2><ul>${list}</ul></section>`
}).join('')

const css = `
:root{--bg:#fff;--text:#1c1b19;--muted:#6b6a66;--accent:#7a5230;--border:#e3e1dc;--code:#f4f2ee}
@media (prefers-color-scheme:dark){:root{--bg:#171513;--text:#ece8e1;--muted:#a19d95;--accent:#d3a574;--border:#34312c;--code:#232019}}
*{box-sizing:border-box}body{margin:0;font:16px/1.6 system-ui,sans-serif;background:var(--bg);color:var(--text);display:grid;grid-template-columns:20rem 1fr;min-height:100vh}
nav{border-inline-end:1px solid var(--border);padding:1rem;overflow:auto;position:sticky;top:0;max-height:100vh}
nav h2{font-size:.8rem;text-transform:uppercase;letter-spacing:.06em;color:var(--muted);margin:1.2rem 0 .3rem}nav ul{list-style:none;margin:0;padding:0}nav li a{display:block;padding:.15rem .4rem;border-radius:4px;text-decoration:none;color:var(--text);font-size:.92rem}
nav a[aria-current]{background:var(--code);font-weight:600}#filter{width:100%;padding:.4rem;border:1px solid var(--border);border-radius:4px;background:var(--bg);color:var(--text)}
main{max-width:52rem;padding:2rem 3rem}a{color:var(--accent)}code{background:var(--code);padding:.1em .3em;border-radius:3px;font-size:.9em}
pre{background:var(--code);padding:1rem;overflow:auto;border-radius:6px}pre code{background:none;padding:0}table{border-collapse:collapse;display:block;overflow:auto}th,td{border:1px solid var(--border);padding:.35rem .6rem;text-align:start}
@media(max-width:800px){body{grid-template-columns:1fr}nav{position:static;max-height:none;border:0;border-block-end:1px solid var(--border)}main{padding:1rem}}
`
const script = `
const box=document.getElementById('filter');box.addEventListener('input',()=>{const q=box.value.trim().toLowerCase();document.querySelectorAll('nav li').forEach(li=>{li.hidden=q!==''&&!li.textContent.toLowerCase().includes(q)});document.querySelectorAll('nav section').forEach(s=>{s.hidden=[...s.querySelectorAll('li')].every(li=>li.hidden)})})
`

if (!checkOnly) {
  rmSync(dist, { recursive: true, force: true })
  mkdirSync(dist, { recursive: true })
}

let count = 0
for (const page of pages) {
  const body = render(page)
  if (checkOnly) continue
  const out = join(dist, htmlName(page.rel))
  mkdirSync(dirname(out), { recursive: true })
  const up = relativeLink(page.rel, '')
  writeFileSync(out, `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${escape(page.title)} · Qahwa docs</title><style>${css}</style></head><body><nav><a href="${up}index.html"><strong>Qahwa</strong> documentation</a><p><input id="filter" type="search" placeholder="Filter pages…" aria-label="Filter pages"></p>${nav(page.rel)}</nav><main>${body}</main><script>${script}</script></body></html>`)
  count++
}

if (!checkOnly) {
  // The front page is the documentation index.
  const home = known.get('README.md')
  writeFileSync(join(dist, 'index.html'), readFileSync(join(dist, htmlName(home.rel)), 'utf8').replace(/href="\.\.\//g, 'href="').replace(/href="(?!https?:|#|mailto:)([^"]*)"/g, 'href="$1"'))
}

if (problems.length > 0) {
  console.error(`${problems.length} broken link(s):\n- ${problems.join('\n- ')}`)
  process.exit(1)
}

console.log(checkOnly ? `Checked ${pages.length} pages: every link works.` : `Built ${count} pages into docs/site/dist.`)
