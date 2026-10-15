import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { Doc, WorkflowStatus } from '../api/types.ts'
import { StatusPill } from '../controls/StatusPill.tsx'

/** Where the document is in its workflow, and the actions the current user may take. */
export function WorkflowBar({ doctype, doc, status, disabled, onError }: {
  doctype: string
  doc: Doc
  status: WorkflowStatus
  /** True while there are unsaved edits: an action works on the saved document. */
  disabled: boolean
  onError: (text: string, conflict: boolean) => void
}) {
  const queryClient = useQueryClient()
  const apply = useMutation({
    mutationFn: (action: string) => api.workflows.apply(doctype, doc.name, action, doc.modified as string | undefined),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['doc', doctype, doc.name] }),
        queryClient.invalidateQueries({ queryKey: ['workflow', doctype, doc.name] }),
        queryClient.invalidateQueries({ queryKey: ['activity', doctype, doc.name] }),
        queryClient.invalidateQueries({ queryKey: ['list', doctype] }),
        queryClient.invalidateQueries({ queryKey: ['workflow', 'pending'] }),
      ])
    },
    onError: (error) => {
      if (!(error instanceof ApiError)) return onError('Could not complete this action. Please try again.', false)
      onError(error.details.map((d) => d.message).join(' ') || error.message, error.status === 409)
    },
  })

  return (
    <div className="workflow-bar" role="group" aria-label="Workflow">
      <StatusPill text={status.state} color={status.color} />
      {status.actions.map((a) => (
        <button key={a.action} className="btn" disabled={disabled || apply.isPending} title={disabled ? 'Save your changes first' : `Moves it to ${a.to}`}
          onClick={() => apply.mutate(a.action)}>
          {a.action}
        </button>
      ))}
      {status.actions.length === 0 && <span className="muted">Nothing for you to do here.</span>}
    </div>
  )
}
