import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { BundleKind, BundleReport } from '../api/types.ts'
import { KINDS, MAX_BYTES, exportUrl, readBundle, summary } from '../lib/bundle.ts'
import { useToast } from '../controls/Toast.tsx'

/** Export what was built in the Desk as one file, and import such a file into another site. */
export function BundlePage() {
  const toast = useToast()
  const [name, setName] = useState('')
  const [kinds, setKinds] = useState<BundleKind[]>(KINDS.map((k) => k.value))
  const [bundle, setBundle] = useState<unknown>(null)
  const [fileName, setFileName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [report, setReport] = useState<BundleReport | null>(null)

  const run = useMutation({
    mutationFn: ({ content, dryRun }: { content: unknown; dryRun: boolean }) => api.bundle.import(content, dryRun),
    onSuccess: (r) => {
      setReport(r)
      if (!r.dryRun) toast(summary(r))
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'The import failed.'),
  })

  const choose = async (file: File | undefined) => {
    setReport(null)
    setError(null)
    setBundle(null)
    if (!file) return
    if (file.size > MAX_BYTES) { setError('The file is too large (5 MB at most).'); return }
    const read = readBundle(await file.text())
    if ('error' in read) { setError(read.error); return }
    setBundle(read.bundle)
    setFileName(file.name)
    run.mutate({ content: read.bundle, dryRun: true })
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Export &amp; Import</h1>
          <p className="muted">Move what you built (DocTypes, customizations, workflows and rules) to another site as one file. Documents, users and webhooks (they hold secrets) are not included. Importing shows a preview first and checks every item as if you had typed it in.</p>
        </div>
      </div>

      <section className="card pad">
        <h2>Export</h2>
        <label className="field">Name of the bundle<input value={name} maxLength={100} onChange={(e) => setName(e.target.value)} placeholder="e.g. Shop setup" /></label>
        <fieldset className="checks"><legend>What to include</legend>
          {KINDS.map((k) => (
            <label key={k.value}><input type="checkbox" checked={kinds.includes(k.value)} onChange={(e) => setKinds(e.target.checked ? [...kinds, k.value] : kinds.filter((x) => x !== k.value))} /> {k.label} <small className="muted">{k.help}</small></label>
          ))}
        </fieldset>
        <a className={`btn primary${kinds.length === 0 ? ' disabled' : ''}`} href={kinds.length === 0 ? undefined : exportUrl(name, kinds)} download="qahwa-bundle.json" aria-disabled={kinds.length === 0}>Download bundle</a>
      </section>

      <section className="card pad">
        <h2>Import</h2>
        <label className="field">Bundle file<input type="file" accept="application/json,.json" onChange={(e) => void choose(e.target.files?.[0])} /></label>
        {error && <div className="banner error" role="alert">{error}</div>}
        {report && (
          <>
            <p role="status"><strong>{report.dryRun ? 'Preview: ' : 'Result: '}</strong>{summary(report)}{fileName && ` (${fileName})`}</p>
            <table className="grid">
              <thead><tr><th>Kind</th><th>DocType</th><th>Action</th><th>Result</th></tr></thead>
              <tbody>
                {report.items.map((i, n) => (
                  <tr key={n} className={i.ok ? undefined : 'row-error'}><td>{i.kind}</td><td>{i.docType}</td><td>{i.action || '—'}</td><td>{i.ok ? i.message : <span role="alert">{i.message}</span>}</td></tr>
                ))}
              </tbody>
            </table>
            {report.dryRun && report.applied > 0 && (
              <button className="btn primary" disabled={run.isPending} onClick={() => run.mutate({ content: bundle, dryRun: false })}>Apply {report.applied} item{report.applied === 1 ? '' : 's'}</button>
            )}
          </>
        )}
      </section>
    </>
  )
}
