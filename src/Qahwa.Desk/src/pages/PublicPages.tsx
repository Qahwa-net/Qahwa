import { useQuery } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { api } from '../api/client.ts'
import { sanitizeHtml } from '../lib/richText.ts'

/** The pages published for anyone (no sign-in): an index, newest first. */
export function PublicPagesIndex() {
  const index = useQuery({ queryKey: ['public-pages'], queryFn: api.publicPages.index, retry: false, staleTime: 0 })
  if (index.isPending) return <p className="center-screen" role="status">Loading…</p>
  const pages = index.data ?? []
  return (
    <main className="public-page">
      <h1>Pages</h1>
      {index.isError && <div className="banner info" role="alert">The pages are not available right now.</div>}
      {!index.isError && pages.length === 0 && <p className="muted">There is nothing here yet.</p>}
      <ul className="plain-list">
        {pages.map((p) => (
          <li key={p.route}>
            <h2><Link to={`/pages/${encodeURIComponent(p.route)}`}>{p.title}</Link></h2>
            {p.publishedOn && <small className="muted">{new Date(p.publishedOn).toLocaleDateString()}</small>}
            {p.summary && <p>{p.summary}</p>}
          </li>
        ))}
      </ul>
    </main>
  )
}

/** One published page. The server cleaned its HTML when it sent it and the Desk cleans it once more before showing it. */
export function PublicPageView() {
  const { route = '' } = useParams()
  const page = useQuery({ queryKey: ['public-page', route], queryFn: () => api.publicPages.get(route), retry: false, staleTime: 0 })
  if (page.isPending) return <p className="center-screen" role="status">Loading…</p>
  if (page.isError || !page.data) return <main className="public-page"><div className="banner info" role="alert">This page is not available. <Link to="/pages">All pages</Link></div></main>
  const { title, html, publishedOn } = page.data
  return (
    <main className="public-page">
      <nav className="breadcrumb" aria-label="Breadcrumb"><Link to="/pages">Pages</Link> <span aria-hidden="true">›</span> <span>{title}</span></nav>
      <h1>{title}</h1>
      {publishedOn && <small className="muted">{new Date(publishedOn).toLocaleDateString()}</small>}
      <div className="form-html public-page-body" dangerouslySetInnerHTML={{ __html: sanitizeHtml(html) }} />
    </main>
  )
}
