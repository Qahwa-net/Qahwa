import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client.ts'

/** Follow or unfollow a document: followers are told about changes and comments. Shows how many follow. */
export function FollowButton({ doctype, name }: { doctype: string; name: string }) {
  const queryClient = useQueryClient()
  const key = ['follow', doctype, name]
  const state = useQuery({ queryKey: key, queryFn: () => api.follow.get(doctype, name), retry: false })
  const toggle = useMutation({
    mutationFn: () => (state.data?.following ? api.follow.unset(doctype, name) : api.follow.set(doctype, name)),
    onSuccess: (next) => queryClient.setQueryData(key, next),
  })

  if (!state.data) return null
  return (
    <button className="btn" aria-pressed={state.data.following} disabled={toggle.isPending} onClick={() => toggle.mutate()}
      title={state.data.following ? 'You are told about changes and comments. Click to stop.' : 'Get told about changes and comments'}>
      {state.data.following ? 'Following ✓' : 'Follow'}{state.data.followers > 0 && <small className="muted"> · {state.data.followers}</small>}
    </button>
  )
}
