import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import { Icon } from '../controls/Icon.tsx'
import { Modal } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import { MAX_RECIPIENTS, parseAddresses } from '../lib/email.ts'

/** Email about a document, from a template or written by hand. The message is queued and sent by the server. */
export function EmailDialog({ doctype, name, onClose }: { doctype: string; name: string; onClose: () => void }) {
  const queryClient = useQueryClient()
  const toast = useToast()
  const templates = useQuery({ queryKey: ['email', 'templates', doctype], queryFn: () => api.email.templates(doctype), retry: false })
  const [to, setTo] = useState('')
  const [template, setTemplate] = useState('')
  const [subject, setSubject] = useState('')
  const [message, setMessage] = useState('')
  const [error, setError] = useState<string | null>(null)
  const { valid, invalid } = parseAddresses(to)

  const fail = (e: unknown) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Something went wrong. Please try again.')
  const fill = useMutation({
    mutationFn: (t: string) => api.email.preview(doctype, name, t),
    onSuccess: (p) => { setSubject(p.subject); setMessage(p.message); setError(null) },
    onError: fail,
  })
  const send = useMutation({
    mutationFn: () => api.email.send(doctype, name, { to: valid, subject: subject.trim(), message: message.trim() }),
    onSuccess: async (r) => {
      toast(r.queued === 1 ? 'Email queued' : `${r.queued} emails queued`)
      await queryClient.invalidateQueries({ queryKey: ['activity', doctype, name] })
      onClose()
    },
    onError: fail,
  })
  const ready = valid.length > 0 && valid.length <= MAX_RECIPIENTS && invalid.length === 0 && subject.trim() !== '' && message.trim() !== ''

  return (
    <Modal title="Email" subtitle={`About ${doctype === name ? doctype : `${doctype} ${name}`}. Plain text; it is sent from the server.`} onClose={onClose} dismissible={!send.isPending}
      footer={<>
        <button className="btn" onClick={onClose} disabled={send.isPending}>Cancel</button>
        <button className="btn primary" form="email-form" disabled={!ready || send.isPending}><Icon name="mail" /> {send.isPending ? 'Sending…' : 'Send'}</button>
      </>}>
      {error && <div className="banner error" role="alert">{error}</div>}
      <form id="email-form" className="modal-form" onSubmit={(e) => { e.preventDefault(); if (ready && !send.isPending) send.mutate() }}>
        <div className="field">
          <label htmlFor="email-to">To</label>
          <input id="email-to" data-autofocus placeholder="name@example.com, another@example.com" value={to} onChange={(e) => setTo(e.target.value)} aria-invalid={invalid.length > 0} />
          {invalid.length > 0 && <small className="error-text" role="status">Not an email address: {invalid.join(', ')}</small>}
          {valid.length > MAX_RECIPIENTS && <small className="error-text" role="status">At most {MAX_RECIPIENTS} addresses.</small>}
        </div>
        {templates.data && templates.data.length > 0 && (
          <div className="field">
            <label htmlFor="email-template">Template <span className="muted">(optional)</span></label>
            <select id="email-template" value={template} disabled={fill.isPending} onChange={(e) => { setTemplate(e.target.value); if (e.target.value) fill.mutate(e.target.value) }}>
              <option value="">— write it myself —</option>
              {templates.data.map((t) => <option key={t.name} value={t.name}>{t.name}</option>)}
            </select>
          </div>
        )}
        <div className="field">
          <label htmlFor="email-subject">Subject</label>
          <input id="email-subject" maxLength={200} value={subject} onChange={(e) => setSubject(e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor="email-message">Message</label>
          <textarea id="email-message" rows={7} maxLength={20000} value={message} onChange={(e) => setMessage(e.target.value)} />
        </div>
      </form>
    </Modal>
  )
}
