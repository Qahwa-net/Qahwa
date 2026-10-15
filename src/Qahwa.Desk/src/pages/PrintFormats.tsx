import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { LetterHeadDetail } from '../api/types.ts'
import { Icon } from '../controls/Icon.tsx'
import { LinkSelect } from '../controls/LinkSelect.tsx'
import { ConfirmDialog } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import { hasColumn } from '../lib/fields.ts'

/** Print Formats and Letter Heads: how documents look on paper. */
export function PrintFormatsPage() {
  const [tab, setTab] = useState<'formats' | 'heads'>('formats')
  return (
    <>
      <div className="page-head">
        <div>
          <h1>Print Formats</h1>
          <p className="muted">Design how a document looks on paper. A format is HTML with <code>{'{{field}}'}</code> where values go; values are always escaped. Without a format a clean layout is used.</p>
        </div>
      </div>
      <div className="tabs" role="tablist">
        <button role="tab" aria-selected={tab === 'formats'} className={tab === 'formats' ? 'on' : undefined} onClick={() => setTab('formats')}>Formats</button>
        <button role="tab" aria-selected={tab === 'heads'} className={tab === 'heads' ? 'on' : undefined} onClick={() => setTab('heads')}>Letter heads</button>
      </div>
      {tab === 'formats' ? <Formats /> : <LetterHeads />}
    </>
  )
}

function message(e: unknown, fallback: string) {
  return e instanceof ApiError ? (e.details[0]?.message ?? e.message) : fallback
}

function Formats() {
  const list = useQuery({ queryKey: ['print', 'formats'], queryFn: api.print.formats, retry: false })
  const [editing, setEditing] = useState<string | 'new' | null>(null)
  if (list.isError) return <div className="banner error" role="alert">Only a System Manager can manage print formats.</div>
  return (
    <div className="perm-layout">
      <aside>
        <button className="btn" onClick={() => setEditing('new')}><Icon name="plus" /> New format</button>
        <ul className="perm-list">
          {list.data?.map((f) => (
            <li key={f.name} className={editing === f.name ? 'active' : undefined}>
              <button className="link-button" onClick={() => setEditing(f.name)}>{f.name}<small className="muted"> {f.docType}{f.isDefault ? ' · default' : ''}</small></button>
            </li>
          ))}
          {list.data?.length === 0 && <li className="muted">No formats yet.</li>}
        </ul>
      </aside>
      <section>{editing ? <FormatEditor key={editing} name={editing === 'new' ? null : editing} onDone={() => setEditing(null)} /> : <p className="muted">Pick a format, or make a new one.</p>}</section>
    </div>
  )
}

const EXAMPLE = '<h1>{{label:customer}}: {{customer}}</h1>\n<p>{{doctype}} {{name}} &middot; {{today}}</p>\n'

function FormatEditor({ name, onDone }: { name: string | null; onDone: () => void }) {
  const queryClient = useQueryClient()
  const toast = useToast()
  const existing = useQuery({ queryKey: ['print', 'format', name], queryFn: () => api.print.format(name!), enabled: name !== null, retry: false })
  const metas = useQuery({ queryKey: ['metas'], queryFn: api.metas })
  const heads = useQuery({ queryKey: ['print', 'heads'], queryFn: api.print.letterHeads, retry: false })
  if (name !== null && !existing.data) return <p className="muted">Loading…</p>
  return <EditorBody initial={existing.data ?? null} doctypes={(metas.data ?? []).filter((m) => m.module !== 'Core').map((m) => m.name)} heads={(heads.data ?? []).map((h) => h.name)}
    onSaved={async () => { await queryClient.invalidateQueries({ queryKey: ['print'] }); toast('Format saved') }} onDeleted={async () => { await queryClient.invalidateQueries({ queryKey: ['print'] }); toast('Format deleted'); onDone() }} />
}

function EditorBody({ initial, doctypes, heads, onSaved, onDeleted }: {
  initial: { name: string; docType: string; html: string; isDefault: boolean; letterHead?: string | null } | null
  doctypes: string[]
  heads: string[]
  onSaved: () => Promise<void>
  onDeleted: () => Promise<void>
}) {
  const [name, setName] = useState(initial?.name ?? '')
  const [docType, setDocType] = useState(initial?.docType ?? '')
  const [html, setHtml] = useState(initial?.html ?? EXAMPLE)
  const [isDefault, setIsDefault] = useState(initial?.isDefault ?? false)
  const [letterHead, setLetterHead] = useState(initial?.letterHead ?? '')
  const [sample, setSample] = useState('')
  const [preview, setPreview] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [deleting, setDeleting] = useState(false)
  const meta = useQuery({ queryKey: ['meta', docType], queryFn: () => api.meta(docType), enabled: docType !== '', retry: false })
  const fields = (meta.data?.data.fields ?? []).filter((f) => hasColumn(f.type) && f.type !== 'Password')
  const tables = (meta.data?.data.fields ?? []).filter((f) => f.type === 'Table')

  const save = useMutation({
    mutationFn: () => api.print.saveFormat({ name: name.trim(), docType, html, isDefault, letterHead: letterHead || null }),
    onSuccess: async () => { setError(null); await onSaved() },
    onError: (e) => setError(message(e, 'Could not save the format.')),
  })
  const run = useMutation({
    mutationFn: () => api.print.preview({ docType, name: sample, html, letterHead: letterHead || null }),
    onSuccess: (page) => { setPreview(page); setError(null) },
    onError: (e) => { setPreview(null); setError(message(e, 'Could not preview.')) },
  })
  const remove = useMutation({ mutationFn: () => api.print.removeFormat(initial!.name), onSuccess: onDeleted })
  const insert = (text: string) => setHtml((h) => `${h}${h.endsWith('\n') || h === '' ? '' : '\n'}${text}`)

  return (
    <>
      {error && <div className="banner error" role="alert">{error}</div>}
      <div className="form-grid">
        <div className="field">
          <label htmlFor="pf-name">Name</label>
          <input id="pf-name" value={name} disabled={initial !== null} maxLength={100} onChange={(e) => setName(e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor="pf-doctype">DocType</label>
          <select id="pf-doctype" value={docType} disabled={initial !== null} onChange={(e) => { setDocType(e.target.value); setSample('') }}>
            <option value="">— choose —</option>
            {doctypes.map((d) => <option key={d}>{d}</option>)}
          </select>
        </div>
        <div className="field">
          <label htmlFor="pf-head">Letter head</label>
          <select id="pf-head" value={letterHead} onChange={(e) => setLetterHead(e.target.value)}>
            <option value="">Default</option>
            {heads.map((h) => <option key={h}>{h}</option>)}
          </select>
        </div>
        <label className="switch"><input type="checkbox" checked={isDefault} onChange={(e) => setIsDefault(e.target.checked)} /><span>Use by default for {docType || 'this DocType'}</span></label>

        <div className="field full">
          <label htmlFor="pf-html">Format (HTML)</label>
          <textarea id="pf-html" className="mono" rows={14} spellCheck={false} value={html} onChange={(e) => setHtml(e.target.value)} />
          <div className="insert-row">
            <select aria-label="Insert a field" className="compact" value="" onChange={(e) => { if (e.target.value) insert(`{{${e.target.value}}}`) }}>
              <option value="">Insert a field…</option>
              {['name', 'doctype', 'today', 'user', 'status', ...fields.map((f) => f.name)].map((f) => <option key={f}>{f}</option>)}
            </select>
            <select aria-label="Insert a table" className="compact" value="" onChange={(e) => {
              const t = e.target.value
              const child = tables.find((x) => x.name === t)
              if (t && child) insert(`<table>\n{{#each ${t}}}\n<tr><td>{{@index}}</td></tr>\n{{/each}}\n</table>`)
            }}>
              <option value="">Insert a table…</option>
              {tables.map((t) => <option key={t.name} value={t.name}>{t.label || t.name}</option>)}
            </select>
          </div>
          <small className="muted">Use <code>{'{{field}}'}</code>, <code>{'{{label:field}}'}</code>, <code>{'{{#if field}}…{{else}}…{{/if}}'}</code> and <code>{'{{#each table}}…{{/each}}'}</code>. Scripts, forms and event attributes are not allowed.</small>
        </div>

        <div className="field full">
          <label>Preview on a document</label>
          <div className="filter-row preview-row">
            <LinkSelect ariaLabel="Sample document" placeholder={docType ? `Choose a ${docType}…` : 'Choose a DocType first'} value={sample} cacheKey={['print-sample', docType]} readOnly={!docType}
              search={(text) => api.linkSearch(docType, text)} onChange={(v) => setSample(v)} />
            <button type="button" className="btn small" disabled={!docType || !sample || run.isPending} onClick={() => run.mutate()}><Icon name="play" size={14} /> {run.isPending ? 'Working…' : 'Preview'}</button>
          </div>
          {preview !== null && <iframe className="print-frame" title="Format preview" sandbox="" srcDoc={preview} />}
        </div>
      </div>
      <div className="form-actions">
        {initial && <button className="btn danger" onClick={() => setDeleting(true)}><Icon name="trash" /> Delete</button>}
        <span className="spacer" />
        <button className="btn primary" disabled={save.isPending || !name.trim() || !docType} onClick={() => save.mutate()}><Icon name="save" /> {save.isPending ? 'Saving…' : 'Save format'}</button>
      </div>
      {deleting && <ConfirmDialog title="Delete this format?" tone="danger" confirmLabel="Delete" busy={remove.isPending} onClose={() => setDeleting(false)} onConfirm={() => remove.mutate()}
        message={`Documents will be printed with the default layout instead.`} />}
    </>
  )
}

function LetterHeads() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const list = useQuery({ queryKey: ['print', 'heads'], queryFn: api.print.letterHeads, retry: false })
  const [draft, setDraft] = useState<(LetterHeadDetail & { isNew: boolean }) | null>(null)
  const [error, setError] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: () => api.print.saveLetterHead({ name: draft!.name.trim(), header: draft!.header, footer: draft!.footer, isDefault: draft!.isDefault }),
    onSuccess: async () => { setError(null); setDraft(null); toast('Letter head saved'); await queryClient.invalidateQueries({ queryKey: ['print'] }) },
    onError: (e) => setError(message(e, 'Could not save the letter head.')),
  })
  const remove = useMutation({ mutationFn: (n: string) => api.print.removeLetterHead(n), onSuccess: async () => { setDraft(null); toast('Letter head deleted'); await queryClient.invalidateQueries({ queryKey: ['print'] }) } })
  if (list.isError) return <div className="banner error" role="alert">Only a System Manager can manage letter heads.</div>
  return (
    <div className="perm-layout">
      <aside>
        <button className="btn" onClick={() => setDraft({ name: '', header: '', footer: '', isDefault: false, isNew: true })}><Icon name="plus" /> New letter head</button>
        <ul className="perm-list">
          {list.data?.map((h) => <li key={h.name} className={draft?.name === h.name ? 'active' : undefined}><button className="link-button" onClick={() => setDraft({ ...h, isNew: false })}>{h.name}{h.isDefault ? <small className="muted"> · default</small> : null}</button></li>)}
          {list.data?.length === 0 && <li className="muted">No letter heads yet.</li>}
        </ul>
      </aside>
      <section>
        {!draft && <p className="muted">A letter head is the header and footer printed on every page of a document: a logo, your address, a thank-you line.</p>}
        {draft && (
          <>
            {error && <div className="banner error" role="alert">{error}</div>}
            <div className="form-grid">
              <div className="field"><label htmlFor="lh-name">Name</label><input id="lh-name" value={draft.name} disabled={!draft.isNew} maxLength={100} onChange={(e) => setDraft({ ...draft, name: e.target.value })} /></div>
              <label className="switch"><input type="checkbox" checked={draft.isDefault} onChange={(e) => setDraft({ ...draft, isDefault: e.target.checked })} /><span>Use by default</span></label>
              <div className="field full"><label htmlFor="lh-header">Header (HTML)</label><textarea id="lh-header" className="mono" rows={5} value={draft.header} onChange={(e) => setDraft({ ...draft, header: e.target.value })} /></div>
              <div className="field full"><label htmlFor="lh-footer">Footer (HTML)</label><textarea id="lh-footer" className="mono" rows={3} value={draft.footer} onChange={(e) => setDraft({ ...draft, footer: e.target.value })} /></div>
            </div>
            <div className="form-actions">
              {!draft.isNew && <button className="btn danger" disabled={remove.isPending} onClick={() => remove.mutate(draft.name)}><Icon name="trash" /> Delete</button>}
              <span className="spacer" />
              <button className="btn primary" disabled={save.isPending || !draft.name.trim()} onClick={() => save.mutate()}><Icon name="save" /> {save.isPending ? 'Saving…' : 'Save letter head'}</button>
            </div>
          </>
        )}
      </section>
    </div>
  )
}
