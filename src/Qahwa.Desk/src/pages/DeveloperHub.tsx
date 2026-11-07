import { useState } from 'react'
import { Link } from 'react-router-dom'
import { HUB, filterHub } from '../lib/developerHub.ts'

/** One page that lists every developer and administrator tool, so none of them has to be remembered by name. */
export function DeveloperHubPage() {
  const [query, setQuery] = useState('')
  const groups = filterHub(HUB, query)
  return (
    <>
      <h1>Developer Hub</h1>
      <p className="muted">Everything for building on Qahwa in one place. Start with the DocType Designer; its API tab gives ready-made calls for each DocType.</p>
      <input type="search" aria-label="Find a tool" placeholder="Find a tool: webhook, cron, permission…" value={query} onChange={(e) => setQuery(e.target.value)} />
      {groups.length === 0 && <p className="muted" role="status">No tool matches “{query}”.</p>}
      {groups.map((g) => (
        <section key={g.title} className="card">
          <h2>{g.title}</h2>
          <ul className="hub-list">
            {g.tools.map((t) => (
              <li key={t.to}>
                {t.to.startsWith('/api/') ? <a href={t.to} target="_blank" rel="noreferrer">{t.title}</a> : <Link to={t.to}>{t.title}</Link>}
                <span className="muted"> {t.about}</span>
              </li>
            ))}
          </ul>
        </section>
      ))}
    </>
  )
}
