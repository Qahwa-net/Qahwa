import { useMutation, useQuery } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'

/** The page an emailed approve/reject link opens: shows what it would do and asks for a click to do it. */
export function WorkflowLinkPage() {
  const { token = '' } = useParams()
  const info = useQuery({ queryKey: ['workflow-link', token], queryFn: () => api.workflows.linkInfo(token), retry: false, staleTime: 0 })
  const confirm = useMutation({ mutationFn: () => api.workflows.linkConfirm(token) })

  if (info.isPending) return <p className="muted" role="status">Loading…</p>
  if (info.isError) {
    return (
      <div className="card" style={{ maxInlineSize: '32rem' }}>
        <h2>This link cannot be used</h2>
        <p className="muted">It may have been used already, have expired, or the document has moved on. It is also only for the person it was sent to.</p>
        <Link to="/">Go to the home page</Link>
      </div>
    )
  }

  const { docType, name, title, state, action, to } = info.data
  const href = `/app/${encodeURIComponent(docType)}/${encodeURIComponent(name)}`
  if (confirm.isSuccess) {
    return (
      <div className="card" style={{ maxInlineSize: '32rem' }}>
        <h2>Done</h2>
        <p role="status">{action} done on {docType} {name}.</p>
        <Link to={href}>Open the document</Link>
      </div>
    )
  }

  return (
    <div className="card" style={{ maxInlineSize: '32rem' }}>
      <h2>{action}?</h2>
      <p><strong>{docType} {name}</strong>{title ? ` (${title})` : ''}<br />
        <span className="muted">It is "{state}" now and would move to "{to}".</span></p>
      {confirm.isError && (
        <div className="banner error" role="alert">
          {confirm.error instanceof ApiError ? (confirm.error.details[0]?.message ?? confirm.error.message) : 'Could not do this. Please try again.'}
        </div>
      )}
      <div className="comment-actions">
        <Link to={href}>Open the document first</Link>
        <button className="btn primary" disabled={confirm.isPending} onClick={() => confirm.mutate()}>{confirm.isPending ? 'Working…' : `Yes, ${action.toLowerCase()}`}</button>
      </div>
    </div>
  )
}
