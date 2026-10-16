import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { RestoreReport } from '../api/types.ts'
import { useToast } from '../controls/Toast.tsx'
import { restoreSummary } from '../lib/backup.ts'

/** Download a backup of the documents, and add what is missing from one back. Administrators only. */
export function BackupPage() {
  const toast = useToast()
  const [file, setFile] = useState<File | null>(null)
  const [report, setReport] = useState<RestoreReport | null>(null)
  const [error, setError] = useState<string | null>(null)

  const run = useMutation({
    mutationFn: ({ chosen, dryRun }: { chosen: File; dryRun: boolean }) => api.backup.restore(chosen, dryRun),
    onSuccess: (r) => {
      setReport(r)
      if (!r.dryRun) toast(restoreSummary(r))
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'The restore failed.'),
  })

  const choose = (chosen: File | undefined) => {
    setReport(null)
    setError(null)
    setFile(chosen ?? null)
    if (chosen) run.mutate({ chosen, dryRun: true })
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Backup &amp; Restore</h1>
          <p className="muted">A backup holds your documents and everything built in the Desk. It does <strong>not</strong> hold passwords, two-factor secrets, sessions, uploaded file contents or queued emails. People set a password again after a restore, and the files folder needs its own copy.</p>
        </div>
      </div>

      <section className="card pad">
        <h2>Backup</h2>
        <p className="muted">Reads every document, so it can take a while on a large system. Keep the file safe: it contains all your data.</p>
        <a className="btn primary" href="/api/backup/download" download>Download a backup</a>
        <p className="muted">A backup can also be written every day to a folder on the server (see the manual: <code>Qahwa:Backup:Path</code>).</p>
      </section>

      <section className="card pad">
        <h2>Restore</h2>
        <p className="muted">Restoring only <strong>adds</strong>: anything that already exists is left alone, nothing is overwritten or deleted. A preview comes first.</p>
        <label className="field">Backup file<input type="file" accept=".zip,application/zip" onChange={(e) => choose(e.target.files?.[0])} /></label>
        {run.isPending && <p role="status" className="muted">Reading the backup…</p>}
        {error && <div className="banner error" role="alert">{error}</div>}
        {report && (
          <>
            <p role="status"><strong>{report.dryRun ? 'Preview: ' : 'Result: '}</strong>{restoreSummary(report)}</p>
            {report.definitions.some((d) => !d.ok) && <div className="banner error" role="alert">Some definitions were refused: {report.definitions.filter((d) => !d.ok).map((d) => `${d.docType} (${d.message})`).join('; ')}</div>}
            <table className="grid">
              <thead><tr><th>DocType</th><th>{report.dryRun ? 'Would add' : 'Added'}</th><th>Already there</th><th>Refused</th><th>Note</th></tr></thead>
              <tbody>
                {report.tables.filter((t) => t.inserted + t.existing + t.failed > 0).map((t) => (
                  <tr key={t.docType} className={t.failed > 0 ? 'row-error' : undefined}><td>{t.docType}</td><td>{t.inserted}</td><td>{t.existing}</td><td>{t.failed}</td><td>{t.firstProblem ?? ''}</td></tr>
                ))}
              </tbody>
            </table>
            {report.dryRun && file && report.tables.some((t) => t.inserted > 0) && (
              <button className="btn primary" disabled={run.isPending} onClick={() => run.mutate({ chosen: file, dryRun: false })}>Restore what is missing</button>
            )}
          </>
        )}
      </section>
    </>
  )
}
