import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client.ts'
import { IconButton } from '../controls/Icon.tsx'

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
  const following = state.data.following
  return (
    <IconButton icon="bell" filled={following} pressed={following} count={Number(state.data.followers)} disabled={toggle.isPending}
      label={following ? 'Following: click to stop getting updates' : 'Follow to get updates and comments'} onClick={() => toggle.mutate()} />
  )
}
