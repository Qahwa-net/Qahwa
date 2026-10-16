import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client.ts'
import { IconButton } from '../controls/Icon.tsx'

/** Like or unlike a document; shows how many people like it. */
export function LikeButton({ doctype, name }: { doctype: string; name: string }) {
  const queryClient = useQueryClient()
  const key = ['like', doctype, name]
  const state = useQuery({ queryKey: key, queryFn: () => api.like.get(doctype, name), retry: false })
  const toggle = useMutation({
    mutationFn: () => (state.data?.liked ? api.like.unset(doctype, name) : api.like.set(doctype, name)),
    onSuccess: (next) => queryClient.setQueryData(key, next),
  })

  if (!state.data) return null
  const liked = state.data.liked
  return (
    <IconButton icon="heart" filled={liked} pressed={liked} count={Number(state.data.count)} disabled={toggle.isPending}
      label={liked ? 'You like this: click to take it back' : 'Like'} onClick={() => toggle.mutate()} />
  )
}
