import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import { useToast } from '../controls/Toast.tsx'

/** The applications you have allowed to use your account, and a way to take that back. */
export function ConnectedAppsSection() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const connected = useQuery({ queryKey: ['oauth-connected'], queryFn: api.oauth.connected, retry: false })
  const revoke = useMutation({
    mutationFn: (clientId: string) => api.oauth.disconnect(clientId),
    onSuccess: async () => { toast('Access withdrawn'); await queryClient.invalidateQueries({ queryKey: ['oauth-connected'] }) },
    onError: (e) => toast(e instanceof ApiError ? e.message : 'Something went wrong.'),
  })

  if (!connected.data || connected.data.length === 0) return null
  return (
    <section className="card narrow" aria-labelledby="connected-title">
      <h2 id="connected-title">Applications you allowed</h2>
      <ul className="plain">
        {connected.data.map((c) => (
          <li key={c.clientId} className="session-row">
            <div><strong>{c.name}</strong><br /><small className="muted">{c.scope} · since {new Date(c.since).toLocaleDateString()}</small></div>
            <button className="btn small" disabled={revoke.isPending} onClick={() => revoke.mutate(c.clientId)}>Withdraw access</button>
          </li>
        ))}
      </ul>
    </section>
  )
}
