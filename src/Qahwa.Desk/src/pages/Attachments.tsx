import { useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import { Icon, IconButton } from '../controls/Icon.tsx'
import { useToast } from '../controls/Toast.tsx'
import { fileProblem, formatSize } from '../lib/files.ts'

/** Files attached to a document: add by choosing or dropping files, open or download, remove. */
export function Attachments({ doctype, name, canUpload }: { doctype: string; name: string; canUpload: boolean }) {
  const queryClient = useQueryClient()
  const toast = useToast()
  const key = ['files', doctype, name]
  const list = useQuery({ queryKey: key, queryFn: () => api.files.list(doctype, name), retry: false })
  const [error, setError] = useState<string | null>(null)
  const [over, setOver] = useState(false)
  const input = useRef<HTMLInputElement>(null)

  const upload = useMutation({
    mutationFn: async (files: File[]) => {
      const problems = files.map((f) => fileProblem(f)).filter(Boolean)
      if (problems.length > 0) throw new Error(problems[0]!)
      for (const file of files) await api.files.upload(doctype, name, file)
      return files.length
    },
    onSuccess: async (count) => {
      setError(null)
      toast(count === 1 ? 'File attached' : `${count} files attached`)
      await Promise.all([queryClient.invalidateQueries({ queryKey: key }), queryClient.invalidateQueries({ queryKey: ['activity', doctype, name] })])
    },
    onError: async (e) => {
      setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : e instanceof Error ? e.message : 'Could not upload.')
      await queryClient.invalidateQueries({ queryKey: key })
    },
  })
  const remove = useMutation({
    mutationFn: (id: string) => api.files.remove(id),
    onSuccess: async () => { toast('File removed'); await Promise.all([queryClient.invalidateQueries({ queryKey: key }), queryClient.invalidateQueries({ queryKey: ['activity', doctype, name] })]) },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'Could not remove the file.'),
  })

  const files = list.data ?? []
  if (list.isError || (!canUpload && files.length === 0)) return null

  return (
    <section className="rail-card attachments" aria-label="Attachments">
      <header>
        <h3>Attachments</h3>
        {canUpload && <IconButton icon="plus" label="Attach files" disabled={upload.isPending} onClick={() => input.current?.click()} />}
      </header>
      {error && <div className="banner error" role="alert">{error}</div>}
      <ul className="file-list">
        {files.map((f) => (
          <li key={f.id}>
            {f.image
              ? <a href={api.files.url(f.id)} target="_blank" rel="noopener noreferrer" className="thumb"><img src={api.files.url(f.id)} alt="" loading="lazy" /></a>
              : <span className="thumb file-icon" aria-hidden="true"><Icon name="download" /></span>}
            <span className="person-text">
              <a href={api.files.url(f.id)} {...(f.image ? { target: '_blank', rel: 'noopener noreferrer' } : { download: f.fileName })}>{f.fileName}</a>
              <small className="muted">{formatSize(f.size)} · {f.uploadedByName || f.uploadedBy}</small>
            </span>
            {canUpload && <IconButton icon="trash" label={`Remove ${f.fileName}`} tone="danger" disabled={remove.isPending} onClick={() => remove.mutate(f.id)} />}
          </li>
        ))}
      </ul>
      {canUpload && (
        <div className={`dropzone${over ? ' over' : ''}`}
          onDragOver={(e) => { e.preventDefault(); setOver(true) }} onDragLeave={() => setOver(false)}
          onDrop={(e) => { e.preventDefault(); setOver(false); const dropped = [...e.dataTransfer.files]; if (dropped.length) upload.mutate(dropped) }}>
          {upload.isPending ? 'Uploading…' : <>Drop files here or <button type="button" className="link-button" onClick={() => input.current?.click()}>choose files</button></>}
        </div>
      )}
      <input ref={input} type="file" multiple hidden aria-label="Choose files to attach"
        onChange={(e) => { const chosen = [...(e.target.files ?? [])]; e.target.value = ''; if (chosen.length) upload.mutate(chosen) }} />
    </section>
  )
}
