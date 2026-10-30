import { useEffect, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import { IconButton } from '../controls/Icon.tsx'
import { Modal } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import { emptyDesk, moveId, ordered, type DeskLayout, type DeskState } from '../lib/desk.ts'
import { Select } from '../controls/Select.tsx'

export function DeskEditor({ state, manager, onClose, onPreview }: { state: DeskState; manager: boolean; onClose: () => void; onPreview: (layout: DeskLayout | null) => void }) {
  const [draft, setDraft] = useState<DeskLayout>(() => structuredClone(state.layout))
  const [tab, setTab] = useState<'sidebar' | 'home' | 'groups'>('sidebar')
  const [search, setSearch] = useState('')
  const [role, setRole] = useState('')
  const [roleVersion, setRoleVersion] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [dragged, setDragged] = useState<string | null>(null)
  const queryClient = useQueryClient(), toast = useToast()
  const roles = useQuery({ queryKey: ['desk-roles'], queryFn: () => api.list('Role', { fields: ['name', 'role_name'], filters: [], orderBy: 'role_name', descending: false, offset: 0, limit: 1000 }), enabled: manager, retry: false })
  useEffect(() => { onPreview(draft) }, [draft, onPreview])
  const links = ordered(state.links, draft.order, l => l.id)
  const ids = links.map(l => l.id)
  const groups = [...new Set(['Favorites', ...state.links.map(l => l.group)])]
  const toggle = (key: 'pinned' | 'hidden' | 'homeShortcuts' | 'collapsedGroups', id: string) => setDraft(d => ({ ...d, [key]: d[key].includes(id) ? d[key].filter(x => x !== id) : [...d[key], id], ...(key === 'pinned' && !d.pinned.includes(id) ? { hidden: d.hidden.filter(x => x !== id) } : {}) }))
  async function switchRole(next: string) {
    setBusy(true); setError(null)
    try {
      if (!next) { setDraft(structuredClone(state.layout)); setRoleVersion(null) }
      else { const result = await api.desk.role(next); setDraft(result.layout); setRoleVersion(result.modified) }
      setRole(next)
    } catch (e) { setError(e instanceof ApiError ? e.message : 'Could not load role defaults.') }
    finally { setBusy(false) }
  }
  async function save(reset = false) {
    setBusy(true); setError(null)
    try {
      if (role) {
        if (reset) { await api.desk.resetRole(role); setDraft(emptyDesk()); setRoleVersion(null) }
        else { const result = await api.desk.saveRole(role, draft, roleVersion); setDraft(result.layout); setRoleVersion(result.modified) }
        await queryClient.invalidateQueries({ queryKey: ['desk-settings'] })
        toast(reset ? 'Role defaults removed' : 'Role defaults saved for users without personal settings')
      } else {
        const next = reset ? await api.desk.reset() : await api.desk.save(draft, state.modified)
        queryClient.setQueriesData<DeskState>({ queryKey: ['desk-settings'] }, old => old ? next : old)
        toast(reset ? 'Desk restored to defaults' : 'Your Desk was saved')
        onPreview(null); onClose()
      }
    } catch (e) { setError(e instanceof ApiError ? e.message : 'Could not save Desk settings.') }
    finally { setBusy(false) }
  }
  function orderButtons(id: string, values: string[], key: 'order' | 'groupOrder' | 'pinned' | 'homeShortcuts') {
    const index = values.indexOf(id)
    return <div className="desk-reorder"><IconButton icon="arrowUp" label={`Move ${state.links.find(l => l.id === id)?.label ?? id} up`} disabled={busy || index <= 0 || Boolean(search)} onClick={() => setDraft(d => ({ ...d, [key]: moveId(values, id, -1) }))} /><IconButton icon="arrowDown" label={`Move ${state.links.find(l => l.id === id)?.label ?? id} down`} disabled={busy || index < 0 || index === values.length - 1 || Boolean(search)} onClick={() => setDraft(d => ({ ...d, [key]: moveId(values, id, 1) }))} /></div>
  }
  return <Modal title="Customize Desk" subtitle="Choose what you see. Your permissions always apply." size="lg" onClose={() => { onPreview(null); onClose() }} dismissible={!busy}
    footer={<div className="desk-editor-footer"><button type="button" className="btn" disabled={busy} onClick={() => void save(true)}>{role ? 'Remove role defaults' : 'Reset to default'}</button><span className="spacer" /><button type="button" className="btn" disabled={busy} onClick={() => { onPreview(null); onClose() }}>Cancel</button><button type="button" className="btn primary" disabled={busy} onClick={() => void save()}>{busy ? 'Saving…' : role ? 'Save role defaults' : 'Save my Desk'}</button></div>}>
    <div className="form-stack">
      {manager && <label className="field"><span>Customize for</span><Select value={role} disabled={busy} onChange={e => void switchRole(e.target.value)}><option value="">My Desk</option>{roles.data?.data.map(r => <option key={r.name} value={r.name}>Role default: {String(r.role_name ?? r.name)}</option>)}</Select></label>}
      <p className="muted small-text">{role ? 'Role defaults apply to members who have not saved a personal Desk. If several roles have defaults, the first role alphabetically is used.' : state.personal ? 'You are editing your personal layout. Reset restores your role defaults.' : state.defaultRole ? `Starting from defaults for ${state.defaultRole}. Saving creates your personal layout.` : 'Starting from the standard Desk. Saving creates your personal layout.'}</p>
      {error && <div className="banner error" role="alert">{error}</div>}
      <div className="tabs" role="group" aria-label="Desk settings">{(['sidebar', 'groups', 'home'] as const).map(t => <button key={t} type="button" aria-pressed={tab === t} className={tab === t ? 'on' : undefined} onClick={() => { setTab(t); setSearch('') }}>{t === 'sidebar' ? 'Sidebar & favorites' : t === 'groups' ? 'Groups' : 'Home page'}</button>)}</div>
      {tab === 'sidebar' && <>
        <p className="muted small-text">Pin links to Favorites or hide menu items. Drag rows or use arrows to reorder. The sidebar previews your changes.</p>
        {draft.pinned.length > 0 && <section><h3>Favorites order</h3>{draft.pinned.map(id => <div className="desk-order-row" key={id}><span>{state.links.find(l => l.id === id)?.label ?? id}</span>{orderButtons(id, draft.pinned, 'pinned')}</div>)}</section>}
        <input type="search" aria-label="Find Desk items" placeholder="Find a page, document, report or saved view…" value={search} onChange={e => setSearch(e.target.value)} />
        <ul className="desk-item-list">{links.filter(l => `${l.label} ${l.group}`.toLowerCase().includes(search.toLowerCase())).map(link => <li key={link.id} draggable={!busy && !search} onDragStart={() => setDragged(link.id)} onDragEnd={() => setDragged(null)} onDragOver={e => e.preventDefault()} onDrop={e => { e.preventDefault(); if (!dragged || dragged === link.id) return; const next = ids.filter(id => id !== dragged); next.splice(next.indexOf(link.id), 0, dragged); setDraft(d => ({ ...d, order: next })); setDragged(null) }}>
          <div className="desk-item-name"><strong>{link.label}</strong><small>{link.group}</small></div>
          {link.defaultVisible && <label className="desk-check"><input type="checkbox" checked={!draft.hidden.includes(link.id)} disabled={busy} onChange={() => toggle('hidden', link.id)} /> Show</label>}
          <label className="desk-check"><input type="checkbox" checked={draft.pinned.includes(link.id)} disabled={busy} onChange={() => toggle('pinned', link.id)} /> Pin</label>
          {orderButtons(link.id, ids, 'order')}
        </li>)}</ul>
      </>}
      {tab === 'groups' && <><p className="muted small-text">Arrange sidebar sections and choose which start collapsed.</p>{ordered(groups, draft.groupOrder, x => x).map(group => <div className="desk-order-row" key={group}><span>{group}</span><label className="desk-check"><input type="checkbox" disabled={busy} checked={draft.collapsedGroups.includes(group)} onChange={() => toggle('collapsedGroups', group)} /> Collapsed</label>{orderButtons(group, ordered(groups, draft.groupOrder, x => x), 'groupOrder')}</div>)}</>}
      {tab === 'home' && <>
        <label className="field"><span>Default workspace</span><Select value={draft.defaultWorkspace ?? ''} disabled={busy} onChange={e => setDraft(d => ({ ...d, defaultWorkspace: e.target.value || null }))}><option value="">First available workspace</option>{state.workspaces.map(w => <option key={w.id} value={w.id}>{w.title}</option>)}</Select></label>
        <h3>My shortcuts</h3><p className="muted small-text">These appear above your workspace on Home.</p>
        {draft.homeShortcuts.map(id => <div key={id} className="desk-order-row"><span>{state.links.find(l => l.id === id)?.label ?? id}</span>{orderButtons(id, draft.homeShortcuts, 'homeShortcuts')}<IconButton icon="x" label="Remove home shortcut" disabled={busy} onClick={() => toggle('homeShortcuts', id)} /></div>)}
        <label className="field"><span>Add shortcut</span><Select value="" disabled={busy} onChange={e => { if (e.target.value) toggle('homeShortcuts', e.target.value) }}><option value="">Choose a page, report or saved view…</option>{state.links.filter(l => !draft.homeShortcuts.includes(l.id)).map(l => <option value={l.id} key={l.id}>{l.label} · {l.group}</option>)}</Select></label>
      </>}
    </div>
  </Modal>
}
