import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import { ConfirmDialog } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import { parseHosts, validSiteName } from '../lib/sites.ts'

/** The sites this server runs: add one (it starts answering at once) or stop serving one. Main-site Administrators only (ADR-148). */
export function SitesPage() {
  const toast = useToast()
  const client = useQueryClient()
  const sites = useQuery({ queryKey: ['sites'], queryFn: api.sites.list, retry: false })
  const [name, setName] = useState('')
  const [hosts, setHosts] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [removing, setRemoving] = useState<string | null>(null)

  const message = (e: unknown, fallback: string) => (e instanceof ApiError ? (e.details[0]?.message ?? e.message) : fallback)

  const create = useMutation({
    mutationFn: () => api.sites.create({ name, hosts: parseHosts(hosts), adminEmail: email, adminPassword: password }),
    onSuccess: (made) => {
      setName(''); setHosts(''); setEmail(''); setPassword(''); setError(null)
      toast(`Site ${made.name} is running.`)
      void client.invalidateQueries({ queryKey: ['sites'] })
    },
    onError: (e) => setError(message(e, 'The site could not be made.')),
  })

  const remove = useMutation({
    mutationFn: (site: string) => api.sites.remove(site),
    onSuccess: (_, site) => {
      setRemoving(null)
      toast(`Site ${site} no longer answers. Its database and files were kept.`)
      void client.invalidateQueries({ queryKey: ['sites'] })
    },
    onError: (e) => { setRemoving(null); setError(message(e, 'The site could not be removed.')) },
  })

  if (sites.isError) {
    return <div className="banner error" role="alert">Sites can be managed only by an Administrator of the main site, and only when the server is set up for it (<code>Qahwa:Sites:Manage</code>).</div>
  }

  const ready = validSiteName(name) && parseHosts(hosts).length > 0 && email.includes('@') && password.length > 0

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Sites</h1>
          <p className="muted">Each site has a database of its own and answers to its own host names. A new site starts answering as soon as it is made; removing one only stops it answering: its database and files are not deleted.</p>
        </div>
      </div>
      {error && <div className="banner error" role="alert">{error}</div>}

      <section className="card pad">
        <h2>Running sites</h2>
        {sites.isPending && <p className="muted" role="status">Loading…</p>}
        {sites.data && sites.data.length === 0 && <p className="muted">No extra sites are running. The main site answers every address.</p>}
        {sites.data && sites.data.length > 0 && (
          <div className="table-wrap">
            <table>
              <thead><tr><th>Site</th><th>Host names</th><th /></tr></thead>
              <tbody>
                {sites.data.map((s) => (
                  <tr key={s.name}>
                    <td>{s.name}</td>
                    <td className="mono">{s.hosts.join(', ')}</td>
                    <td className="num"><button className="btn danger" onClick={() => { setError(null); setRemoving(s.name) }}>Stop serving</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="card pad">
        <h2>Add a site</h2>
        <form onSubmit={(e) => { e.preventDefault(); setError(null); create.mutate() }}>
          <label className="field"><span>Name <span className="muted">(lower-case letters, digits and dashes; it names the database)</span></span>
            <input value={name} maxLength={40} required onChange={(e) => setName(e.target.value)} aria-invalid={name !== '' && !validSiteName(name)} /></label>
          <label className="field"><span>Host names <span className="muted">(one per line; <code>*.acme.example.com</code> covers one level)</span></span>
            <textarea rows={3} required value={hosts} onChange={(e) => setHosts(e.target.value)} /></label>
          <label className="field"><span>First administrator's email</span>
            <input type="email" autoComplete="off" required value={email} onChange={(e) => setEmail(e.target.value)} /></label>
          <label className="field"><span>Password</span>
            <input type="password" autoComplete="new-password" required value={password} onChange={(e) => setPassword(e.target.value)} /></label>
          <button className="btn primary" type="submit" disabled={create.isPending || !ready}>{create.isPending ? 'Making the site…' : 'Add the site'}</button>
        </form>
      </section>

      {removing && (
        <ConfirmDialog title={`Stop serving ${removing}?`} tone="danger" confirmLabel="Stop serving" busy={remove.isPending}
          message="People can no longer reach this site, and its background work stops. Its database and files stay where they are, and its settings file is renamed so a restart does not bring it back."
          onConfirm={() => remove.mutate(removing)} onClose={() => setRemoving(null)} />
      )}
    </>
  )
}
