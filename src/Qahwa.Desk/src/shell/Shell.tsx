import { useEffect, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, NavLink, Outlet, useLocation } from 'react-router-dom'
import { api } from '../api/client.ts'
import type { MetaSummary } from '../api/types.ts'
import { useAuth } from '../auth.tsx'
import { useI18n } from '../i18n.tsx'
import { setDisplaySettings } from '../lib/display.ts'
import { LanguageSwitcher } from './LanguageSwitcher.tsx'
import { NotificationBell } from './NotificationBell.tsx'
import { ThemeToggle } from './ThemeToggle.tsx'
import { useTheme } from '../theme.tsx'

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
  const { t, applyDefault } = useI18n()
  const { brand } = useTheme()
  const settings = useQuery({ queryKey: ['display-settings'], queryFn: api.settings, staleTime: 60_000, retry: false })
  if (settings.data) setDisplaySettings(settings.data) // before the page below renders, so dates follow the settings
  const defaultLanguage = settings.data?.defaultLanguage
  useEffect(() => { if (defaultLanguage) applyDefault(defaultLanguage) }, [defaultLanguage, applyDefault])
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
        <Link to="/" className="brand">{brand.logo && <img src={brand.logo} alt="" className="brand-logo" />}<span>{brand.name}</span></Link>
        <span className="spacer" />
        <LanguageSwitcher />
        <ThemeToggle />
        <NotificationBell />
        <Link to="/account" className="user-chip" title={user?.user}>{user?.full_name}</Link>
        <button className="btn" onClick={() => void logout()}>{t('Sign out')}</button>
      </header>

      <nav className={`sidebar${open ? ' open' : ''}`} aria-label="DocTypes">
        {metas.isPending && <p className="muted pad">Loading…</p>}
        {metas.isError && <p className="muted pad">Could not load DocTypes.</p>}
        {metas.data?.length === 0 && <p className="muted pad">You do not have access to any DocType yet.</p>}
        <section>
          <h2>{t('My work')}</h2>
          <ul>
            <li><NavLink to="/dashboards">{t('Dashboards')}</NavLink></li>
            <li><NavLink to="/reports">{t('Reports')}</NavLink></li>
            <li><NavLink to="/auto-repeat">{t('Auto Repeat')}</NavLink></li>
          </ul>
        </section>
        {canDesign && (
          <section>
            <h2>{t('Tools')}</h2>
            <ul>
              <li><NavLink to="/designer">{t('DocType Designer')}</NavLink></li>
              <li><NavLink to="/customize">{t('Customize Form')}</NavLink></li>
              <li><NavLink to="/permissions">{t('Role Permissions')}</NavLink></li>
              <li><NavLink to="/workflows">{t('Workflow Builder')}</NavLink></li>
              <li><NavLink to="/webhooks">{t('Webhooks')}</NavLink></li>
              <li><NavLink to="/bundle">{t('Export & Import')}</NavLink></li>
              {user?.roles.includes('Administrator') && <li><NavLink to="/backup">{t('Backup & Restore')}</NavLink></li>}
              <li><NavLink to="/workspaces">{t('Workspaces')}</NavLink></li>
              <li><NavLink to="/brand">{t('Brand')}</NavLink></li>
              <li><NavLink to="/print-formats">{t('Print Formats')}</NavLink></li>
              <li><NavLink to="/notification-rules">{t('Notification Rules')}</NavLink></li>
              <li><NavLink to="/assignment-rules">{t('Assignment Rules')}</NavLink></li>
              <li><NavLink to="/naming">{t('Naming Series')}</NavLink></li>
            </ul>
          </section>
        )}
        {groups.map(([module, items]) => (
          <section key={module}>
            <h2>{t(module)}</h2>
            <ul>
              {items.map((meta) => (
                <li key={meta.name}>
                  <NavLink to={`/app/${encodeURIComponent(meta.name)}`}>{t(meta.name)}</NavLink>
                </li>
              ))}
            </ul>
          </section>
        ))}
      </nav>

      <main className="content">
        <Outlet key={settings.dataUpdatedAt} />
      </main>
    </div>
  )
}
