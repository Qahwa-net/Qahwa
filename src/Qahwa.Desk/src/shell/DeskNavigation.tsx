import { useState, type DragEvent } from 'react'
import { Link, NavLink, useLocation } from 'react-router-dom'
import { Icon } from '../controls/Icon.tsx'
import { useI18n } from '../i18n.tsx'
import { groupFolded, NOT_A_MODULE, sidebarGroups, withGroupMoved, withLinkMoved, type DeskLayout, type DeskState } from '../lib/desk.ts'

const GROUP = 'application/x-qahwa-group'
const LINK = 'application/x-qahwa-link'

/**
 * The sidebar: every group folds; business modules also open a page of their own (like a Frappe workspace). Groups and the links inside a
 * group can be dragged into the order you like (Customize Desk does the same with buttons, for the keyboard).
 */
export function DeskNavigation({ state, layout, busy, unfolded, onFold, onLayout }: {
  state: DeskState
  layout: DeskLayout
  busy: boolean
  /** Groups unfolded on this device (Tools, Core and long groups start folded). */
  unfolded: ReadonlySet<string>
  onFold: (group: string, fold: boolean) => void
  /** The layout after a drag: the caller saves it. */
  onLayout: (next: DeskLayout) => void
}) {
  const { t } = useI18n()
  const { pathname } = useLocation()
  const [over, setOver] = useState<string | null>(null)
  const groups = sidebarGroups(state.links, layout)
  const names = groups.map(([g]) => g)

  const allow = (event: DragEvent, kind: string, key: string) => {
    if (busy || !event.dataTransfer.types.includes(kind)) return
    event.preventDefault()
    event.dataTransfer.dropEffect = 'move'
    setOver(key)
  }

  return <>{groups.map(([group, links]) => {
    const holdsCurrentPage = links.some(link => link.path.split('?')[0] === pathname) || pathname === `/module/${encodeURIComponent(group)}`
    const folded = groupFolded(group, links.length, layout, unfolded, holdsCurrentPage)
    const ids = links.map(l => l.id)
    return <section key={group} className={`desk-nav-group${over === `g:${group}` ? ' drop-target' : ''}`}
      onDragOver={e => allow(e, GROUP, `g:${group}`)} onDragLeave={() => setOver(null)}
      onDrop={e => {
        setOver(null)
        const dragged = e.dataTransfer.getData(GROUP)
        if (dragged) { e.preventDefault(); onLayout(withGroupMoved(layout, names, dragged, group)) }
      }}>
      <h2 draggable={!busy} onDragStart={e => { e.dataTransfer.setData(GROUP, group); e.dataTransfer.effectAllowed = 'move' }} onDragEnd={() => setOver(null)}>
        <button type="button" className="desk-group-toggle" aria-expanded={!folded} disabled={busy} onClick={() => onFold(group, !folded)}>
          <span>{t(group)}</span><span className="count">{links.length}</span><span className={folded ? 'chev shut' : 'chev'}><Icon name="chevronDown" size={14} /></span>
        </button>
        {!NOT_A_MODULE.has(group) && <Link className="desk-module-link" to={`/module/${encodeURIComponent(group)}`} aria-label={`${t(group)} page`} title={`${t(group)} page`}><Icon name="map" size={14} /></Link>}
      </h2>
      {!folded && <ul>{links.map(link => (
        <li key={link.id} className={over === `l:${link.id}` ? 'drop-target' : undefined} draggable={!busy}
          onDragStart={e => { e.stopPropagation(); e.dataTransfer.setData(LINK, JSON.stringify({ group, id: link.id })); e.dataTransfer.effectAllowed = 'move' }}
          onDragEnd={() => setOver(null)}
          onDragOver={e => { if (e.dataTransfer.types.includes(LINK)) { e.stopPropagation(); allow(e, LINK, `l:${link.id}`) } }}
          onDrop={e => {
            setOver(null)
            const raw = e.dataTransfer.getData(LINK)
            if (!raw) return
            e.stopPropagation()
            e.preventDefault()
            const dragged = JSON.parse(raw) as { group: string; id: string }
            if (dragged.group === group) onLayout(withLinkMoved(layout, group, ids, dragged.id, link.id)) // only inside the same group
          }}>
          <NavLink to={link.path} end={link.path === '/'}>{t(link.label)}</NavLink>
        </li>))}</ul>}
    </section>
  })}</>
}
