import { Link, NavLink, useLocation } from 'react-router-dom'
import { Icon } from '../controls/Icon.tsx'
import { useI18n } from '../i18n.tsx'
import { groupFolded, NOT_A_MODULE, sidebarGroups, type DeskLayout, type DeskState } from '../lib/desk.ts'

/** The sidebar: every group folds and unfolds; business modules also open a page of their own (like a Frappe workspace). */
export function DeskNavigation({ state, layout, busy, unfolded, onFold }: {
  state: DeskState
  layout: DeskLayout
  busy: boolean
  /** Groups unfolded on this device (Tools, Core and long groups start folded). */
  unfolded: ReadonlySet<string>
  onFold: (group: string, fold: boolean) => void
}) {
  const { t } = useI18n()
  const { pathname } = useLocation()
  return <>{sidebarGroups(state.links, layout).map(([group, links]) => {
    const holdsCurrentPage = links.some(link => link.path.split('?')[0] === pathname) || pathname === `/module/${encodeURIComponent(group)}`
    const folded = groupFolded(group, links.length, layout, unfolded, holdsCurrentPage)
    return <section key={group} className="desk-nav-group">
      <h2>
        <button type="button" className="desk-group-toggle" aria-expanded={!folded} disabled={busy} onClick={() => onFold(group, !folded)}>
          <span>{t(group)}</span><span className="count">{links.length}</span><span className={folded ? 'chev shut' : 'chev'}><Icon name="chevronDown" size={14} /></span>
        </button>
        {!NOT_A_MODULE.has(group) && <Link className="desk-module-link" to={`/module/${encodeURIComponent(group)}`} aria-label={`${t(group)} page`} title={`${t(group)} page`}><Icon name="map" size={14} /></Link>}
      </h2>
      {!folded && <ul>{links.map(link => <li key={link.id}><NavLink to={link.path} end={link.path === '/'}>{t(link.label)}</NavLink></li>)}</ul>}
    </section>
  })}</>
}
