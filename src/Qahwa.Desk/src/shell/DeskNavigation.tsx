import { NavLink } from 'react-router-dom'
import { Icon } from '../controls/Icon.tsx'
import { useI18n } from '../i18n.tsx'
import { sidebarGroups, type DeskLayout, type DeskState } from '../lib/desk.ts'

export function DeskNavigation({ state, layout, busy, onCollapse }: { state: DeskState; layout: DeskLayout; busy: boolean; onCollapse: (group: string) => void }) {
  const { t } = useI18n()
  return <>{sidebarGroups(state.links, layout).map(([group, links]) => {
    const collapsed = layout.collapsedGroups.includes(group)
    return <section key={group} className="desk-nav-group">
      <h2><button type="button" className="desk-group-toggle" aria-expanded={!collapsed} disabled={busy} onClick={() => onCollapse(group)}>
        <span>{t(group)}</span><span className={collapsed ? 'chev shut' : 'chev'}><Icon name="chevronDown" size={14} /></span>
      </button></h2>
      {!collapsed && <ul>{links.map(link => <li key={link.id}><NavLink to={link.path} end={link.path === '/'}>{t(link.label)}</NavLink></li>)}</ul>}
    </section>
  })}</>
}
