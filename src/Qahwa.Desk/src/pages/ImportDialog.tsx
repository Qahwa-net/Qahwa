import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { ImportPreview, ImportResult } from '../api/types.ts'
import { Icon } from '../controls/Icon.tsx'
import { Modal } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import { checkImportFile, summarise } from '../lib/importFile.ts'

/** Import documents from a CSV file: choose the file, check it, then import. Every row is created as if you had typed it. */
export function ImportDialog({ doctype, canUpdate, onClose }: { doctype: string; canUpdate: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const toast = useToast()
  const [mode, setMode] = useState<'Insert' | 'Update'>('Insert')
  const [csv, setCsv] = useState<string | null>(null)
  const [fileName, setFileName] = useState('')
  const [stopOnError, setStopOnError] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [preview, setPreview] = useState<ImportPreview | null>(null)
  const [result, setResult] = useState<ImportResult | null>(null)

  const fail = (e: unknown) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Something went wrong. Please try again.')
  const check = useMutation({
    mutationFn: (args: { text: string; mode: string }) => api.import.preview(doctype, args.text, args.mode),
    onSuccess: (p) => { setPreview(p); setError(null) },
    onError: (e) => { setPreview(null); fail(e) },
  })
  const run = useMutation({
    mutationFn: () => api.import.run(doctype, csv!, mode, stopOnError),
    onSuccess: async (r) => {
      setResult(r)
      setError(null)
      toast(summarise(r))
      await queryClient.invalidateQueries({ queryKey: ['list', doctype] })
    },
    onError: fail,
  })

  const choose = async (file: File | undefined) => {
    setPreview(null)
    setResult(null)
    setError(null)
    if (!file) return
    const problem = checkImportFile(file)
    if (problem) {
      setCsv(null)
      setFileName('')
      return setError(problem)
    }
    const text = await file.text()
    setCsv(text)
    setFileName(file.name)
    check.mutate({ text, mode })
  }

  const switchMode = (next: 'Insert' | 'Update') => {
    setMode(next)
    if (csv) check.mutate({ text: csv, mode: next })
  }

  const busy = check.isPending || run.isPending
  const ready = csv !== null && preview !== null && !busy && result === null

  return (
    <Modal title={`Import ${doctype}`} subtitle="Create documents from a CSV file, one per row. The first row names the columns." size="lg" onClose={onClose} dismissible={!run.isPending}
      footer={<>
        <button className="btn" onClick={onClose} disabled={run.isPending}>{result ? 'Close' : 'Cancel'}</button>
        {!result && (
          <button className="btn primary" disabled={!ready} onClick={() => run.mutate()}>
            <Icon name="upload" /> {run.isPending ? 'Importing…' : `Import ${preview?.rows ?? ''} row${preview?.rows === 1 ? '' : 's'}`}
          </button>
        )}
      </>}>
      {error && <div className="banner error" role="alert">{error}</div>}
      <div className="modal-row">
        <div className="field">
          <span className="label" id="import-mode">What to do</span>
          <div className="segmented" role="radiogroup" aria-labelledby="import-mode">
            <button type="button" role="radio" aria-checked={mode === 'Insert'} className={mode === 'Insert' ? 'on' : undefined} disabled={busy} onClick={() => switchMode('Insert')}>Create new</button>
            {canUpdate && (
              <button type="button" role="radio" aria-checked={mode === 'Update'} className={mode === 'Update' ? 'on' : undefined} disabled={busy} onClick={() => switchMode('Update')}>Update existing</button>
            )}
          </div>
        </div>
        <div className="field">
          <label htmlFor="import-file">File</label>
          <input id="import-file" data-autofocus type="file" accept=".csv,.txt,text/csv,text/plain" disabled={busy} onChange={(e) => void choose(e.target.files?.[0])} />
          <small className="muted">
            <a href={api.import.templateUrl(doctype, mode)}>Download a template</a> with the right columns.
            {mode === 'Update' && ' Updating needs a column named name with each document’s ID.'}
          </small>
        </div>
      </div>

      {check.isPending && <p className="muted" role="status">Checking {fileName}…</p>}
      {preview && !result && (
        <>
          <h3 className="modal-section">{fileName}: {preview.rows} row{preview.rows === 1 ? '' : 's'}</h3>
          <div className="table-wrap">
            <table aria-label="Columns and sample rows">
              <thead>
                <tr>
                  {preview.columns.map((c, i) => (
                    <th key={i}>{c.header}<br />{c.field ? <small className="muted">→ {c.label || c.field}</small> : <small className="error-text">ignored</small>}</th>
                  ))}
                </tr>
              </thead>
              <tbody>{preview.sample.map((row, r) => <tr key={r}>{row.map((cell, i) => <td key={i}>{cell}</td>)}</tr>)}</tbody>
            </table>
          </div>
          {preview.errors.length > 0
            ? <Problems errors={preview.errors} more={preview.moreErrors} intro="Problems in the first rows (all rows are checked again when importing):" />
            : <p className="muted">The first rows look fine.</p>}
          <label className="switch"><input type="checkbox" checked={stopOnError} onChange={(e) => setStopOnError(e.target.checked)} /><span>Stop at the first row that fails</span></label>
        </>
      )}
      {result && (
        <>
          <div className={`banner ${result.failed > 0 ? 'info' : 'ok'}`} role="status"><strong>{summarise(result)}.</strong>{result.stopped && ' Stopped at the first failure.'}</div>
          {result.errors.length > 0 && <Problems errors={result.errors} more={result.moreErrors} intro="These rows were not imported:" />}
        </>
      )}
    </Modal>
  )
}

function Problems({ errors, more, intro }: { errors: { row: number; field?: string | null; message: string }[]; more: boolean; intro: string }) {
  return (
    <div className="import-problems">
      <p>{intro}</p>
      <ul>
        {errors.map((e, i) => <li key={i}>Row {e.row}{e.field ? `, ${e.field}` : ''}: {e.message}</li>)}
      </ul>
      {more && <p className="muted">…and more.</p>}
    </div>
  )
}
