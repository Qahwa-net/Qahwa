import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client.ts'

/** The emails sent about a document and the replies that came back to it, oldest first. Shown only when there are some. */
export function ConversationPanel({ doctype, name }: { doctype: string; name: string }) {
  const thread = useQuery({ queryKey: ['conversation', doctype, name], queryFn: () => api.conversation.forDocument(doctype, name), retry: false })
  const items = thread.data ?? []
  if (items.length === 0) return null
  return (
    <section className="conversation" aria-label="Emails">
      <h3 className="modal-section">Emails</h3>
      <ol className="conversation-list">
        {items.map((m, i) => (
          <li key={`${m.at}-${i}`} className={`conversation-item ${m.kind}`}>
            <strong>{m.subject || '(no subject)'}</strong>
            <small className="muted">
              {m.kind === 'sent' ? `To ${m.who}` : `From ${m.whoName ? `${m.whoName} <${m.who}>` : m.who}`} · {new Date(m.at).toLocaleString()}
              {m.status && m.status !== 'Sent' ? ` · ${m.status === 'Failed' ? 'could not be sent' : 'waiting to be sent'}` : ''}
            </small>
            <p className="conversation-text">{m.text}</p>
          </li>
        ))}
      </ol>
    </section>
  )
}
