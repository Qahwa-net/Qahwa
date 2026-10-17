import { useEffect, useRef, useState } from 'react'
import type { PDFDocumentProxy } from 'pdfjs-dist'
import workerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url'
import type { AttachedFile } from '../api/types.ts'
import { api } from '../api/client.ts'
import { filePreviewKind } from '../lib/files.ts'
import { Modal } from './Modal.tsx'
import { IconButton } from './Icon.tsx'

export function FilePreview({ file, onClose }: { file: AttachedFile; onClose: () => void }) {
  const kind = filePreviewKind(file)
  const [imageError, setImageError] = useState(false)
  return <Modal title={file.fileName} subtitle="Attachment preview" onClose={onClose} size="xl">
    {kind === 'image' ? imageError ? <p className="banner error" role="alert">The image could not be loaded. It may have been removed or your access may have changed.</p> : <div className="file-image-stage"><img src={api.files.url(file.id)} alt={file.fileName} onError={() => setImageError(true)} /></div>
      : kind === 'pdf' ? <PdfPreview key={file.id} id={file.id} /> : <p>This file type cannot be previewed. <a href={api.files.url(file.id)} download={file.fileName}>Download {file.fileName}</a></p>}
  </Modal>
}
function PdfPreview({ id }: { id: string }) {
  const [pdf, setPdf] = useState<PDFDocumentProxy | null>(null)
  const [pageNumber, setPageNumber] = useState(1), [zoom, setZoom] = useState(1)
  const [error, setError] = useState<string | null>(null), [rendering, setRendering] = useState(false)
  const canvas = useRef<HTMLCanvasElement>(null), stage = useRef<HTMLDivElement>(null)
  const [width, setWidth] = useState(640)
  useEffect(() => {
    const observer = new ResizeObserver(() => { if (stage.current) setWidth(stage.current.clientWidth) })
    if (stage.current) observer.observe(stage.current)
    return () => observer.disconnect()
  }, [])
  useEffect(() => {
    let cancelled = false
    let destroy: (() => void) | undefined
    void import('pdfjs-dist').then(async lib => {
      if (cancelled) return
      lib.GlobalWorkerOptions.workerSrc = workerUrl
      const loading = lib.getDocument({ url: api.files.url(id), withCredentials: true, disableFontFace: true, useSystemFonts: true, enableXfa: false })
      destroy = () => { void loading.destroy() }
      const doc = await loading.promise
      if (!cancelled) setPdf(doc)
    }).catch(() => { if (!cancelled) setError('This PDF could not be opened. It may be damaged, password protected, or no longer accessible.') })
    return () => { cancelled = true; destroy?.() }
  }, [id])
  useEffect(() => {
    if (!pdf || !canvas.current) return
    let cancelled = false
    let cancel: (() => void) | undefined
    void pdf.getPage(pageNumber).then(async page => {
      if (cancelled || !canvas.current) return
      setRendering(true); setError(null)
      const natural = page.getViewport({ scale: 1 })
      const scale = Math.max(.2, Math.min((width - 32) / natural.width, 1.25)) * zoom
      const viewport = page.getViewport({ scale })
      const ratio = Math.min(window.devicePixelRatio || 1, 2)
      const target = canvas.current
      target.width = Math.floor(viewport.width * ratio); target.height = Math.floor(viewport.height * ratio)
      target.style.width = viewport.width + 'px'; target.style.height = viewport.height + 'px'
      const task = page.render({ canvas: target, viewport, transform: ratio === 1 ? undefined : [ratio, 0, 0, ratio, 0, 0] })
      cancel = () => task.cancel()
      await task.promise
      if (!cancelled) setRendering(false)
    }).catch(() => { if (!cancelled) { setError('Could not render this PDF page.'); setRendering(false) } })
    return () => { cancelled = true; cancel?.() }
  }, [pdf, pageNumber, zoom, width])
  return <div className="pdf-preview">
    <div className="pdf-toolbar">
      <IconButton icon="arrowLeft" label="Previous page" disabled={!pdf || pageNumber <= 1} onClick={() => setPageNumber(n => n - 1)} />
      <span>Page {pageNumber} of {pdf?.numPages ?? '…'}</span>
      <IconButton icon="arrowRight" label="Next page" disabled={!pdf || pageNumber >= pdf.numPages} onClick={() => setPageNumber(n => n + 1)} />
      <span className="spacer" /><IconButton icon="minus" label="Zoom out" disabled={zoom <= .5} onClick={() => setZoom(z => Math.max(.5, z - .25))} /><span>{Math.round(zoom * 100)}%</span><IconButton icon="plus" label="Zoom in" disabled={zoom >= 2} onClick={() => setZoom(z => Math.min(2, z + .25))} />
    </div>
    {error && <p className="banner error" role="alert">{error}</p>}
    {(!pdf || rendering) && !error && <small role="status">Loading PDF…</small>}
    <div className="pdf-stage" ref={stage}><canvas ref={canvas} aria-label={`PDF page ${pageNumber}`} role="img" /></div>
  </div>
}
