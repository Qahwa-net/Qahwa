import { Navigate, Route, Routes, useLocation } from 'react-router-dom'
import { AuthProvider, useAuth } from './auth.tsx'
import { Shell } from './shell/Shell.tsx'
import { AccountPage } from './pages/AccountPage.tsx'
import { DesignerEditor } from './pages/DesignerEditor.tsx'
import { DesignerList } from './pages/DesignerList.tsx'
import { FormView } from './pages/FormView.tsx'
import { HomePage } from './pages/HomePage.tsx'
import { PermissionManagerPage } from './pages/PermissionManager.tsx'
import { ListView } from './pages/ListView.tsx'
import { LoginPage } from './pages/LoginPage.tsx'

function RequireAuth() {
  const { user } = useAuth()
  const location = useLocation()

  if (user === undefined) return <div className="center-screen" role="status">Loading…</div>
  if (user === null) {
    const next = location.pathname + location.search
    return <Navigate to={`/login?next=${encodeURIComponent(next)}`} replace />
  }
  return <Shell />
}

export function App() {
  return (
    <AuthProvider>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route element={<RequireAuth />}>
          <Route index element={<HomePage />} />
          <Route path="account" element={<AccountPage />} />
          <Route path="designer" element={<DesignerList />} />
          <Route path="designer/new" element={<DesignerEditor />} />
          <Route path="designer/:name" element={<DesignerEditor />} />
          <Route path="permissions" element={<PermissionManagerPage />} />
          <Route path="permissions/:name" element={<PermissionManagerPage />} />
          <Route path="app/:doctype" element={<ListView />} />
          <Route path="app/:doctype/new" element={<FormView />} />
          <Route path="app/:doctype/:name" element={<FormView />} />
        </Route>
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </AuthProvider>
  )
}
