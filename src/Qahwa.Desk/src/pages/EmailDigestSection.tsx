import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import { useToast } from '../controls/Toast.tsx'

/** A daily or weekly email to yourself with your unread notifications and open assignments (nothing is sent when there is nothing to say). */
export function EmailDigestSection() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const digest = useQuery({ queryKey: ['email-digest'], queryFn: api.emailDigest.get, retry: false })
  const save = useMutation({
    mutationFn: api.emailDigest.set,
    onSuccess: (next) => { queryClient.setQueryData(['email-digest'], next); toast('Saved') },
    onError: (e) => toast(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Something went wrong.'),
  })

  if (!digest.data) return null
  const d = digest.data
  return (
    <section className="card narrow" aria-labelledby="digest-title">
      <h2 id="digest-title">Email digest</h2>
      <p className="muted small-text">Get your unread notifications and open assignments by email. Nothing is sent when there is nothing to tell you.</p>
      <label className="check-line">
        <input type="checkbox" checked={d.enabled} disabled={save.isPending} onChange={(e) => save.mutate({ enabled: e.target.checked })} /> Send me a digest
      </label>
      {d.enabled && (
        <div className="row">
          <label className="field"><span>How often</span>
            <select value={d.frequency} disabled={save.isPending} onChange={(e) => save.mutate({ frequency: e.target.value })}>
              <option>Daily</option><option>Weekly</option>
            </select>
          </label>
          <label className="field"><span>Hour (UTC)</span>
            <input type="number" min={0} max={23} defaultValue={d.sendHour} disabled={save.isPending}
              onBlur={(e) => { const hour = Number(e.target.value); if (Number.isInteger(hour) && hour >= 0 && hour <= 23 && hour !== d.sendHour) save.mutate({ sendHour: hour }) }} />
          </label>
        </div>
      )}
    </section>
  )
}
