import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client.ts'
import { Icon } from '../controls/Icon.tsx'
import { Modal } from '../controls/Modal.tsx'

/** A preview of the printout. The page is shown inside a sandboxed frame that can run nothing; "Open to print" opens it in its own tab. */
export function PrintDialog({ doctype, name, onClose }: { doctype: string; name: string; onClose: () => void }) {
  const choices = useQuery({ queryKey: ['print', 'choices', doctype], queryFn: () => api.print.choices(doctype), retry: false })
  const [format, setFormat] = useState('')
  const [letterHead, setLetterHead] = useState('')
  const url = api.print.url(doctype, name, format, letterHead)
  const page = useQuery({ queryKey: ['print', 'page', url], queryFn: () => api.print.page(url), retry: false, staleTime: 0 })

  return (
    <Modal title="Print" subtitle={`${doctype === name ? doctype : `${doctype} ${name}`}. Choose how it looks, then open it to print or save as PDF.`} size="lg" onClose={onClose}
      footer={<>
        <button className="btn" onClick={onClose}>Close</button>
        <a className="btn primary" href={url} target="_blank" rel="noopener noreferrer"><Icon name="printer" /> Open to print</a>
      </>}>
      <div className="modal-row">
        <div className="field">
          <label htmlFor="print-format">Format</label>
          <select id="print-format" value={format} onChange={(e) => setFormat(e.target.value)}>
            <option value="">Default</option>
            {choices.data?.formats.map((f) => <option key={f.name} value={f.name}>{f.name}{f.isDefault ? ' (default)' : ''}</option>)}
          </select>
        </div>
        <div className="field">
          <label htmlFor="print-letterhead">Letter head</label>
          <select id="print-letterhead" value={letterHead} onChange={(e) => setLetterHead(e.target.value)}>
            <option value="">Default</option>
            <option value="none">None</option>
            {choices.data?.letterHeads.map((h) => <option key={h} value={h}>{h}</option>)}
          </select>
        </div>
      </div>
      {page.isPending && <p className="muted" role="status">Preparing the preview…</p>}
      {page.isError && <div className="banner error" role="alert">Could not prepare the printout.</div>}
      {page.data !== undefined && <iframe className="print-frame" title="Print preview" sandbox="" srcDoc={page.data} />}
    </Modal>
  )
}
