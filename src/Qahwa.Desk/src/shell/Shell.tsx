import { DeskEditor } from '../pages/DeskEditor.tsx'
import { DeskNavigation } from './DeskNavigation.tsx'
import { useDeskSettings, type DeskLayout, type DeskState } from '../lib/desk.ts'
import { useToast } from '../controls/Toast.tsx'
import { Icon } from '../controls/Icon.tsx'
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { Modal } from '../controls/Modal.tsx'
import { SHORTCUTS, isTyping, newDocumentPath, shortcutFor } from '../lib/shortcuts.ts'
import { api } from '../api/client.ts'
import type { MetaSummary } from '../api/types.ts'
import { useAuth } from '../auth.tsx'
import { useI18n } from '../i18n.tsx'
import { setDisplaySettings } from '../lib/display.ts'
import { LanguageSwitcher } from './LanguageSwitcher.tsx'
import { NotificationBell } from './NotificationBell.tsx'
import { CommandPalette } from './CommandPalette.tsx'
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
  const [searching, setSearching] = useState(false)
  const desk = useDeskSettings()
  const [editingDesk, setEditingDesk] = useState(false)
  const [deskPreview, setDeskPreview] = useState<DeskLayout | null>(null)
  const queryClient = useQueryClient()
  const toast = useToast()
  const collapseGroup = useMutation({
    mutationFn: (group: string) => {
      const state = desk.data!
      const collapsedGroups = state.layout.collapsedGroups.includes(group) ? state.layout.collapsedGroups.filter(g => g !== group) : [...state.layout.collapsedGroups, group]
      return api.desk.save({ ...state.layout, collapsedGroups }, state.modified)
    },
    onSuccess: next => queryClient.setQueriesData<DeskState>({ queryKey: ['desk-settings'] }, old => old ? next : old),
    onError: () => { toast('Could not save sidebar state. Reload and try again.'); void queryClient.invalidateQueries({ queryKey: ['desk-settings'] }) },
  })

  const location = useLocation()
  const navigate = useNavigate()
  const [showShortcuts, setShowShortcuts] = useState(false)
  // Ctrl+K (or ⌘K) opens the search box from anywhere; Alt+N starts a new document and ? lists the shortcuts, never while typing.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const action = shortcutFor(event, isTyping(event.target))
      if (action === 'search') {
        event.preventDefault()
        setSearching(true)
      } else if (action === 'help') {
        event.preventDefault()
        setShowShortcuts(true)
      } else if (action === 'new') {
        const target = newDocumentPath(location.pathname)
        if (target) {
          event.preventDefault()
          navigate(target)
        }
      }
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [location.pathname, navigate])
  const contentRef = useRef<HTMLElement>(null)
  // The shell stays mounted across pages; reset only the page, preserving the menu's position.
  useLayoutEffect(() => {
    contentRef.current?.scrollTo({ top: 0, left: 0, behavior: 'instant' })
  }, [location.pathname])
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
        <button type="button" className="btn" aria-label="Search (Ctrl+K)" title="Search (Ctrl+K)" onClick={() => setSearching(true)}>🔍 <span className="btn-label">Search</span></button>
        <LanguageSwitcher />
        <ThemeToggle />
        <NotificationBell />
        <Link to="/account" className="user-chip" title={user?.user}>{user?.full_name}</Link>
        <button className="btn" onClick={() => void logout()}>{t('Sign out')}</button>
      </header>

      <nav className={`sidebar${open ? ' open' : ''}`} aria-label="DocTypes">
        <div className="desk-customize"><button type="button" className="btn small" disabled={!desk.data} onClick={() => setEditingDesk(true)}><Icon name="pencil" size={15} /> {t('Customize Desk')}</button></div>
        {desk.data ? <DeskNavigation state={desk.data} layout={deskPreview ?? desk.data.layout} busy={collapseGroup.isPending || editingDesk} onCollapse={group => collapseGroup.mutate(group)} /> : <>
        {metas.isPending && <p className="muted pad">Loading…</p>}
        {metas.isError && <p className="muted pad">Could not load DocTypes.</p>}
        {metas.data?.length === 0 && <p className="muted pad">You do not have access to any DocType yet.</p>}
        <section>
          <h2>{t('My work')}</h2>
          <ul>
            <li><NavLink to="/dashboards">{t('Dashboards')}</NavLink></li>
            <li><NavLink to="/reports">{t('Reports')}</NavLink></li>
            <li><NavLink to="/script-reports">{t('Script Reports')}</NavLink></li>
            <li><NavLink to="/auto-repeat">{t('Auto Repeat')}</NavLink></li>
          </ul>
        </section>
        {canDesign && (
          <section>
            <h2>{t('Tools')}</h2>
            <ul>
              <li><NavLink to="/designer">{t('DocType Designer')}</NavLink></li>
              <li><NavLink to="/customize">{t('Customize Form')}</NavLink></li>
              <li><NavLink to="/app/Email%20Account">{t('Email Account')}</NavLink></li>
              <li><NavLink to="/permissions">{t('Role Permissions')}</NavLink></li>
              <li><NavLink to="/workflows">{t('Workflow Builder')}</NavLink></li>
              <li><NavLink to="/webhooks">{t('Webhooks')}</NavLink></li>
              <li><NavLink to="/emailed-reports">{t('Emailed Reports')}</NavLink></li>
              <li><NavLink to="/apps">{t('Apps')}</NavLink></li>
              <li><NavLink to="/slow-requests">{t('Slow Requests')}</NavLink></li>
              <li><NavLink to="/oauth-clients">{t('OAuth Applications')}</NavLink></li>
              <li><NavLink to="/app/Scheduled%20Job%20Log">{t('Scheduled Job Log')}</NavLink></li>
              <li><NavLink to="/app/Error%20Log">{t('Error Log')}</NavLink></li>
              <li><NavLink to="/app/Deleted%20Document">{t('Deleted Documents')}</NavLink></li>
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
        </>}
      </nav>

      <main className="content" ref={contentRef}>
        <Outlet key={settings.dataUpdatedAt} />
      </main>
      {editingDesk && desk.data && <DeskEditor state={desk.data} manager={canDesign} onClose={() => { setEditingDesk(false); setDeskPreview(null) }} onPreview={setDeskPreview} />}
      {showShortcuts && (
        <Modal title="Keyboard shortcuts" onClose={() => setShowShortcuts(false)}>
          <dl className="shortcut-list">
            {SHORTCUTS.map((s) => <div key={s.keys}><dt><kbd>{s.keys}</kbd></dt><dd>{s.does}</dd></div>)}
          </dl>
        </Modal>
      )}
      {searching && <CommandPalette metas={metas.data ?? []} canDesign={canDesign} isAdministrator={Boolean(user?.roles.includes('Administrator'))} onClose={() => setSearching(false)} />}
    </div>
  )
}
