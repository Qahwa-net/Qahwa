import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import { IconButton } from '../controls/Icon.tsx'
import { Modal } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import { isPending, resultToCsv, statusText } from '../lib/preparedReports.ts'

const SHOWN_ROWS = 500

/** The versions of one report built in the background: ask for one, watch it finish, open or download it. */
export function PreparedReports({ reportId }: { reportId: string }) {
  const queryClient = useQueryClient()
  const toast = useToast()
  const key = ['prepared-reports', reportId]
  const list = useQuery({
    queryKey: key,
    queryFn: () => api.preparedReports.forReport(reportId),
    retry: false,
    // Look again every few seconds while something is still being built.
    refetchInterval: (query) => (query.state.data?.some(isPending) ? 3000 : false),
  })
  const [openId, setOpenId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const fail = (e: unknown) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Something went wrong. Please try again.')

  const request = useMutation({
    mutationFn: () => api.preparedReports.request(reportId),
    onSuccess: async () => { setError(null); toast('Preparing the report'); await queryClient.invalidateQueries({ queryKey: key }) },
    onError: fail,
  })
  const remove = useMutation({
    mutationFn: (id: string) => api.preparedReports.remove(id),
    onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: key }) },
    onError: fail,
  })
  const items = list.data ?? []
  const waiting = items.some(isPending)

  return (
    <section className="prepared-reports">
      <div className="page-head">
        <h3 className="modal-section">Prepared versions</h3>
        <span className="spacer" />
        <button className="btn" disabled={request.isPending || waiting} onClick={() => request.mutate()}>{waiting ? 'Preparing…' : 'Prepare in the background'}</button>
      </div>
      <p className="muted small-text">For a big report: it is built on the server with all its rows, kept for a week, and you can leave and come back.</p>
      {error && <div className="banner error" role="alert">{error}</div>}
      <ul className="person-list">
        {items.map((r) => (
          <li key={r.id}>
            <span className="person-text">
              <strong>{new Date(r.requested).toLocaleString()}</strong>
              <small className="chips">
                <span className={`chip status-${r.status.toLowerCase()}`}>{statusText(r)}</span>
                {r.message && <span className="chip error-text">{r.message}</span>}
              </small>
            </span>
            <span className="row-actions">
              {r.status === 'Done' && <button className="btn small" onClick={() => setOpenId(r.id)}>Open</button>}
              {!isPending(r) && <IconButton icon="trash" label="Delete this version" tone="danger" disabled={remove.isPending} onClick={() => remove.mutate(r.id)} />}
            </span>
          </li>
        ))}
        {list.data && items.length === 0 && <li className="empty-note">Nothing prepared yet.</li>}
      </ul>
      {openId && <PreparedResult id={openId} onClose={() => setOpenId(null)} />}
    </section>
  )
}

function PreparedResult({ id, onClose }: { id: string; onClose: () => void }) {
  const result = useQuery({ queryKey: ['prepared-report', id], queryFn: () => api.preparedReports.get(id), retry: false })
  const data = result.data

  function download() {
    if (!data) return
    const blob = new Blob(['\uFEFF', resultToCsv(data)], { type: 'text/csv;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = `${(data.info.title ?? 'report').replace(/[^A-Za-z0-9_.-]+/g, '_')}.csv`
    link.click()
    URL.revokeObjectURL(url)
  }

  return (
    <Modal title={data?.info.title ?? 'Prepared report'} subtitle={data ? `${data.info.rows} rows, prepared ${new Date(data.info.requested).toLocaleString()}` : undefined} size="xl" onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Close</button><button className="btn primary" disabled={!data} onClick={download}>Download CSV</button></>}>
      {result.isPending && <p className="muted" role="status">Loading…</p>}
      {result.isError && <div className="banner error" role="alert">This prepared report could not be opened.</div>}
      {data && (
        <>
          {data.info.truncated && <div className="banner info" role="status">The report was cut short at the server's limit. Add a filter to narrow it down.</div>}
          {data.rows.length > SHOWN_ROWS && <div className="banner info" role="status">Showing the first {SHOWN_ROWS} rows. Download the CSV for all {data.rows.length}.</div>}
          <div className="table-wrap">
            <table className="grid">
              <thead><tr>{data.columns.map((c) => <th key={c}>{c}</th>)}</tr></thead>
              <tbody>
                {data.rows.slice(0, SHOWN_ROWS).map((row, i) => (
                  <tr key={i}>{data.columns.map((c) => <td key={c}>{row[c] === null || row[c] === undefined ? '' : String(row[c])}</td>)}</tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </Modal>
  )
}
