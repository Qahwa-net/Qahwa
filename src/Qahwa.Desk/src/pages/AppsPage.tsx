import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import { ConfirmDialog } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'

/** The apps installed on this site: switch one off (its hooks, methods, jobs and reports stop; its data stays) or back on. System Managers. */
export function AppsPage() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const apps = useQuery({ queryKey: ['apps'], queryFn: api.apps.list, retry: false })
  const [error, setError] = useState<string | null>(null)
  const [confirming, setConfirming] = useState<string | null>(null)

  const change = useMutation({
    mutationFn: ({ name, action }: { name: string; action: 'install' | 'uninstall' }) => api.apps.change(name, action),
    onSuccess: async (_, v) => {
      setError(null)
      setConfirming(null)
      toast(v.action === 'install' ? 'App installed' : 'App uninstalled')
      await queryClient.invalidateQueries()
    },
    onError: (e) => { setConfirming(null); setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Something went wrong. Please try again.') },
  })

  if (apps.isError) return <div className="banner error" role="alert">Could not load the apps. You need the System Manager role.</div>
  return (
    <>
      <div className="page-head">
        <div>
          <h1>Apps</h1>
          <p className="muted">Uninstalling stops an app's hooks, functions, scheduled jobs and reports on this site. Its documents and DocTypes stay.</p>
        </div>
      </div>
      {error && <div className="banner error" role="alert">{error}</div>}
      {apps.isPending && <p className="muted" role="status">Loading…</p>}
      {apps.data && apps.data.length === 0 && <p className="empty-note">No apps are installed.</p>}
      <ul className="repeat-page-list">
        {(apps.data ?? []).map((a) => (
          <li key={a.name} className="card">
            <p><strong>{a.title}</strong> <small className="muted">· {a.name} {a.version} · {a.installed ? 'installed' : 'uninstalled'}</small></p>
            <div className="row-actions">
              {a.installed
                ? <button className="btn small danger" disabled={change.isPending} onClick={() => setConfirming(a.name)}>Uninstall</button>
                : <button className="btn small" disabled={change.isPending} onClick={() => change.mutate({ name: a.name, action: 'install' })}>Install</button>}
            </div>
          </li>
        ))}
      </ul>
      {confirming && (
        <ConfirmDialog title={`Uninstall ${confirming}?`} tone="danger" confirmLabel="Uninstall" busy={change.isPending} onClose={() => setConfirming(null)}
          onConfirm={() => change.mutate({ name: confirming, action: 'uninstall' })}
          message="Its hooks, functions, scheduled jobs and reports stop on this site. Its data and DocTypes are kept, and you can install it again." />
      )}
    </>
  )
}
