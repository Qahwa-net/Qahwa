import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, NavLink, Outlet, useLocation } from 'react-router-dom'
import { api } from '../api/client.ts'
import type { MetaSummary } from '../api/types.ts'
import { useAuth } from '../auth.tsx'
import { NotificationBell } from './NotificationBell.tsx'

function groupByModule(metas: MetaSummary[]): [string, MetaSummary[]][] {
  const groups = new Map<string, MetaSummary[]>()
  for (const meta of metas) {
    const key = meta.module || 'Other'
    groups.set(key, [...(groups.get(key) ?? []), meta])
  }
  return [...groups.entries()].sort(([a], [b]) => a.localeCompare(b))
}

export function Shell() {
  const { user, logout } = useAuth()
  const [open, setOpen] = useState(false)
  const location = useLocation()
  const metas = useQuery({ queryKey: ['metas'], queryFn: api.metas })
  const groups = useMemo(() => groupByModule(metas.data ?? []), [metas.data])
  // The server enforces this too; the link is only hidden for people who could not use it.
  const canDesign = Boolean(user?.roles.some((r) => r === 'Administrator' || r === 'System Manager'))

  // Close the mobile menu after navigating.
  const [lastPath, setLastPath] = useState(location.pathname)
  if (lastPath !== location.pathname) {
    setLastPath(location.pathname)
    setOpen(false)
  }

  return (
    <div className="shell">
      <header className="topbar">
        <button className="btn icon menu-toggle" aria-label="Toggle navigation" aria-expanded={open}
          onClick={() => setOpen(!open)}>☰</button>
        <Link to="/" className="brand">Qahwa</Link>
        <span className="spacer" />
        <NotificationBell />
        <Link to="/account" className="user-chip" title={user?.user}>{user?.full_name}</Link>
        <button className="btn" onClick={() => void logout()}>Sign out</button>
      </header>

      <nav className={`sidebar${open ? ' open' : ''}`} aria-label="DocTypes">
        {metas.isPending && <p className="muted pad">Loading…</p>}
        {metas.isError && <p className="muted pad">Could not load DocTypes.</p>}
        {metas.data?.length === 0 && <p className="muted pad">You do not have access to any DocType yet.</p>}
        <section>
          <h2>My work</h2>
          <ul>
            <li><NavLink to="/dashboards">Dashboards</NavLink></li>
            <li><NavLink to="/auto-repeat">Auto Repeat</NavLink></li>
          </ul>
        </section>
        {canDesign && (
          <section>
            <h2>Tools</h2>
            <ul>
              <li><NavLink to="/designer">DocType Designer</NavLink></li>
              <li><NavLink to="/customize">Customize Form</NavLink></li>
              <li><NavLink to="/permissions">Role Permissions</NavLink></li>
              <li><NavLink to="/workflows">Workflow Builder</NavLink></li>
              <li><NavLink to="/print-formats">Print Formats</NavLink></li>
              <li><NavLink to="/notification-rules">Notification Rules</NavLink></li>
              <li><NavLink to="/assignment-rules">Assignment Rules</NavLink></li>
              <li><NavLink to="/naming">Naming Series</NavLink></li>
            </ul>
          </section>
        )}
        {groups.map(([module, items]) => (
          <section key={module}>
            <h2>{module}</h2>
            <ul>
              {items.map((meta) => (
                <li key={meta.name}>
                  <NavLink to={`/app/${encodeURIComponent(meta.name)}`}>{meta.name}</NavLink>
                </li>
              ))}
            </ul>
          </section>
        ))}
      </nav>

      <main className="content">
        <Outlet />
      </main>
    </div>
  )
}
