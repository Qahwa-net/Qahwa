import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { WorkspaceBlock, WorkspaceDetail } from '../api/types.ts'
import { ChipPicker } from '../controls/ChipPicker.tsx'
import { Icon, IconButton } from '../controls/Icon.tsx'
import { ConfirmDialog } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import { PAGES, moveBlock, newBlock, newShortcut, toRequest } from '../lib/workspaces.ts'
import { rowErrors } from '../lib/workflow.ts'

/** Workspaces: arrange the pages people start from, and choose which roles see each. */
export function WorkspacesPage() {
  const list = useQuery({ queryKey: ['workspaces', 'list'], queryFn: api.workspaces.list, retry: false })
  const [editing, setEditing] = useState<string | 'new' | null>(null)
  if (list.isError) return <div className="banner error" role="alert">Only a System Manager can manage workspaces.</div>
  return (
    <>
      <div className="page-head">
        <div>
          <h1>Workspaces</h1>
          <p className="muted">Pages people start from, shown on the home page: headings, shortcut rows and dashboards. Choose which roles see each one; people only see shortcuts they are allowed to use.</p>
        </div>
      </div>
      <div className="perm-layout">
        <aside>
          <button className="btn" onClick={() => setEditing('new')}><Icon name="plus" /> New workspace</button>
          <ul className="perm-list">
            {list.data?.map((w) => (
              <li key={w.id} className={editing === w.id ? 'active' : undefined}>
                <button className="link-button" onClick={() => setEditing(w.id)}>{w.title}<small className="muted"> {w.roles.length ? w.roles.join(', ') : 'everyone'}</small></button>
              </li>
            ))}
            {list.data?.length === 0 && <li className="muted">No workspaces yet.</li>}
          </ul>
        </aside>
        <section>{editing ? <Editor key={editing} id={editing === 'new' ? null : editing} onDone={() => setEditing(null)} /> : <p className="muted">Pick a workspace, or make a new one.</p>}</section>
      </div>
    </>
  )
}

function Editor({ id, onDone }: { id: string | null; onDone: () => void }) {
  const detail = useQuery({ queryKey: ['workspaces', 'detail', id], queryFn: () => api.workspaces.get(id!), enabled: id !== null, retry: false, staleTime: 0 })
  if (id !== null && !detail.data) return <p className="muted">Loading…</p>
  return <Body initial={detail.data ?? null} onDone={onDone} />
}

function Body({ initial, onDone }: { initial: WorkspaceDetail | null; onDone: () => void }) {
  const queryClient = useQueryClient()
  const toast = useToast()
  const metas = useQuery({ queryKey: ['metas'], queryFn: api.metas })
  const dashboards = useQuery({ queryKey: ['dashboards'], queryFn: api.dashboards.list, retry: false })
  const [title, setTitle] = useState(initial?.info.title ?? '')
  const [sequence, setSequence] = useState(initial?.info.sequence ?? 0)
  const [roles, setRoles] = useState<string[]>(initial?.info.roles ?? [])
  const [blocks, setBlocks] = useState<WorkspaceBlock[]>(() => structuredClone(initial?.blocks ?? []))
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [banner, setBanner] = useState<string | null>(null)
  const [deleting, setDeleting] = useState(false)

  const save = useMutation({
    mutationFn: () => {
      const body = toRequest({ title, sequence, roles, blocks })
      return initial ? api.workspaces.update(initial.info.id, body) : api.workspaces.create(body)
    },
    onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ['workspaces'] }); toast('Workspace saved'); onDone() },
    onError: (error) => {
      if (!(error instanceof ApiError)) return setBanner('Could not save the workspace.')
      setErrors(Object.fromEntries(error.details.filter((d) => d.field).map((d) => [d.field, d.message])))
      setBanner(error.details.filter((d) => !d.field).map((d) => d.message).join(' ') || (error.details.length ? 'Please fix the highlighted blocks.' : error.message))
    },
  })
  const remove = useMutation({
    mutationFn: () => api.workspaces.remove(initial!.info.id),
    onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ['workspaces'] }); toast('Workspace deleted'); onDone() },
  })
  const update = (i: number, patch: Partial<WorkspaceBlock>) => setBlocks(blocks.map((b, j) => (j === i ? { ...b, ...patch } : b)))

  return (
    <>
      {banner && <div className="banner error" role="alert">{banner}</div>}
      <div className="form-grid">
        <div className="field">
          <label htmlFor="ws-title">Title</label>
          <input id="ws-title" maxLength={100} value={title} aria-invalid={Boolean(errors.title)} onChange={(e) => setTitle(e.target.value)} />
          {errors.title && <small className="error-text" role="alert">{errors.title}</small>}
        </div>
        <div className="field">
          <label htmlFor="ws-seq">Position</label>
          <input id="ws-seq" type="number" min={0} max={10000} value={sequence} onChange={(e) => setSequence(Number(e.target.value) || 0)} />
          <small className="muted">Smaller numbers come first.</small>
        </div>
        <div className="field full">
          <label>Shown to people with these roles <span className="muted">(none = everyone)</span></label>
          <ChipPicker doctype="Role" values={roles} onChange={setRoles} label="Roles" placeholder="Add a role…" />
          {errors.roles && <small className="error-text" role="alert">{errors.roles}</small>}
        </div>
      </div>

      <h3 className="modal-section">Blocks</h3>
      <ol className="rule-list">
        {blocks.map((block, i) => {
          const e = rowErrors(errors, 'blocks', i)
          return (
            <li key={i} className={`card rule-card${Object.keys(e).length ? ' has-error' : ''}`}>
              <header className="rule-head">
                <span className="rule-no" aria-hidden="true">{i + 1}</span>
                <select aria-label={`Block ${i + 1} type`} value={block.type} onChange={(ev) => update(i, newBlock(ev.target.value as WorkspaceBlock['type']))}>
                  <option value="Heading">Heading</option><option value="Shortcuts">Shortcuts</option><option value="Dashboard">Dashboard</option>
                </select>
                <span className="spacer" />
                <IconButton icon="arrowLeft" label="Move up" className="rot-up" disabled={i === 0} onClick={() => setBlocks(moveBlock(blocks, i, -1))} />
                <IconButton icon="arrowLeft" label="Move down" className="rot-down" disabled={i === blocks.length - 1} onClick={() => setBlocks(moveBlock(blocks, i, 1))} />
                <IconButton icon="trash" label={`Remove block ${i + 1}`} tone="danger" onClick={() => setBlocks(blocks.filter((_, j) => j !== i))} />
              </header>
              {block.type === 'Heading' && (
                <div className="field">
                  <input aria-label={`Block ${i + 1} heading`} maxLength={100} value={block.text ?? ''} aria-invalid={Boolean(e.text)} onChange={(ev) => update(i, { text: ev.target.value })} />
                  {e.text && <small className="error-text" role="alert">{e.text}</small>}
                </div>
              )}
              {block.type === 'Dashboard' && (
                <div className="field">
                  <select aria-label={`Block ${i + 1} dashboard`} value={block.dashboardId ?? ''} aria-invalid={Boolean(e.dashboardId)} onChange={(ev) => update(i, { dashboardId: ev.target.value })}>
                    <option value="">— choose a dashboard —</option>
                    {dashboards.data?.map((d) => <option key={d.id} value={d.id}>{d.title}</option>)}
                  </select>
                  {e.dashboardId && <small className="error-text" role="alert">{e.dashboardId}</small>}
                </div>
              )}
              {block.type === 'Shortcuts' && (
                <div>
                  {e.items && <small className="error-text" role="alert">{e.items}</small>}
                  {block.items.map((s, si) => (
                    <div key={si} className="shortcut-row">
                      <input aria-label={`Block ${i + 1} shortcut ${si + 1} label`} placeholder="Label" maxLength={60} value={s.label} aria-invalid={Boolean(e[`items[${si}].label`])}
                        onChange={(ev) => update(i, { items: block.items.map((x, j) => (j === si ? { ...x, label: ev.target.value } : x)) })} />
                      <select aria-label={`Block ${i + 1} shortcut ${si + 1} kind`} value={s.kind}
                        onChange={(ev) => update(i, { items: block.items.map((x, j) => (j === si ? { ...x, kind: ev.target.value as typeof s.kind, target: '' } : x)) })}>
                        <option value="DocType">Documents of a DocType</option><option value="Dashboard">A dashboard</option><option value="Page">A Desk page</option>
                      </select>
                      <select aria-label={`Block ${i + 1} shortcut ${si + 1} target`} value={s.target} aria-invalid={Boolean(e[`items[${si}].target`])}
                        onChange={(ev) => update(i, { items: block.items.map((x, j) => (j === si ? { ...x, target: ev.target.value, label: x.label || ev.target.selectedOptions[0]?.textContent || '' } : x)) })}>
                        <option value="">— choose —</option>
                        {s.kind === 'DocType' && metas.data?.map((m) => <option key={m.name} value={m.name}>{m.name}</option>)}
                        {s.kind === 'Dashboard' && dashboards.data?.map((d) => <option key={d.id} value={d.id}>{d.title}</option>)}
                        {s.kind === 'Page' && PAGES.map((p) => <option key={p.value} value={p.value}>{p.label}</option>)}
                      </select>
                      <IconButton icon="x" label="Remove shortcut" onClick={() => update(i, { items: block.items.filter((_, j) => j !== si) })} />
                      {(e[`items[${si}].label`] ?? e[`items[${si}].target`]) && <small className="error-text" role="alert">{e[`items[${si}].label`] ?? e[`items[${si}].target`]}</small>}
                    </div>
                  ))}
                  <button type="button" className="btn small" onClick={() => update(i, { items: [...block.items, newShortcut()] })}><Icon name="plus" size={14} /> Add a shortcut</button>
                </div>
              )}
            </li>
          )
        })}
      </ol>
      {blocks.length === 0 && <p className="empty-note">No blocks yet.</p>}
      {errors.blocks && <div className="banner error" role="alert">{errors.blocks}</div>}
      <div className="form-actions">
        <button className="btn" onClick={() => setBlocks([...blocks, newBlock('Shortcuts')])}><Icon name="plus" /> Add a block</button>
        <span className="spacer" />
        {initial && <button className="btn danger" onClick={() => setDeleting(true)}><Icon name="trash" /> Delete</button>}
        <button className="btn primary" disabled={save.isPending || !title.trim()} onClick={() => { setBanner(null); setErrors({}); save.mutate() }}><Icon name="save" /> {save.isPending ? 'Saving…' : 'Save workspace'}</button>
      </div>
      {deleting && <ConfirmDialog title="Delete this workspace?" tone="danger" confirmLabel="Delete" busy={remove.isPending} onClose={() => setDeleting(false)} onConfirm={() => remove.mutate()} message={`"${initial?.info.title}" will disappear from everyone's home page.`} />}
    </>
  )
}
