import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client.ts'
import { durationText } from '../lib/milestones.ts'

/** The path of a tracked field (usually a status) through the life of a document: each value, when, by whom, after how long. */
export function MilestoneStrip({ doctype, name }: { doctype: string; name: string }) {
  const milestones = useQuery({ queryKey: ['milestones', doctype, name], queryFn: () => api.milestones.forDocument(doctype, name), retry: false })
  const view = milestones.data
  if (!view || view.items.length === 0) return null
  return (
    <section className="milestones" aria-label={`${view.label} milestones`}>
      <h3 className="modal-section">{view.label} so far</h3>
      <ol className="milestone-list">
        {view.items.map((m, i) => (
          <li key={`${m.at}-${i}`} className={i === view.items.length - 1 ? 'current' : undefined}>
            <strong>{m.value}</strong>
            <small className="muted" title={m.by}>
              {new Date(m.at).toLocaleString()} · {m.by}{m.afterSeconds != null ? ` · after ${durationText(m.afterSeconds)}` : ''}
            </small>
          </li>
        ))}
      </ol>
    </section>
  )
}
