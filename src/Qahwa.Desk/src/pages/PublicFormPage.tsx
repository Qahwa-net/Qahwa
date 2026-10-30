import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import { inputType, isMultiline, missing, toPayload, type Answers } from '../lib/webform.ts'
import { Select } from '../controls/Select.tsx'

/** A public form: opened by anyone without signing in. Everything shown comes from the form's definition and is rendered as text. */
export function PublicFormPage() {
  const { route = '' } = useParams()
  const form = useQuery({ queryKey: ['public-form', route], queryFn: () => api.publicForms.get(route), retry: false, staleTime: 0 })
  const [answers, setAnswers] = useState<Answers>({})
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [trap, setTrap] = useState('')
  const [banner, setBanner] = useState<string | null>(null)

  const send = useMutation({
    mutationFn: (payload: Record<string, unknown>) => api.publicForms.submit(route, payload),
    onError: (e) => {
      if (e instanceof ApiError && e.details.length > 0) setErrors(Object.fromEntries(e.details.map((d) => [d.field, d.message])))
      setBanner(e instanceof ApiError ? e.message : 'Could not send. Please try again.')
    },
  })

  if (form.isPending) return <p className="center-screen" role="status">Loading…</p>
  if (form.isError || !form.data) return <main className="public-form"><div className="banner info" role="alert">This form is not available.</div></main>
  const { title, intro, fields } = form.data

  if (send.isSuccess) return <main className="public-form"><h1>{title}</h1><div className="banner info" role="status">{send.data.message}</div></main>

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    setBanner(null)
    const empty = missing(fields, answers)
    if (empty.length > 0) {
      setErrors(Object.fromEntries(empty.map((n) => [n, 'Please fill this in.'])))
      return
    }
    setErrors({})
    send.mutate({ ...toPayload(fields, answers), website: trap })
  }

  return (
    <main className="public-form">
      <h1>{title}</h1>
      {intro && <p className="muted" style={{ whiteSpace: 'pre-line' }}>{intro}</p>}
      <form onSubmit={submit} noValidate>
        {banner && <div className="banner error" role="alert">{banner}</div>}
        {fields.map((field) => {
          const id = `f-${field.name}`
          const error = errors[field.name]
          const value = answers[field.name]
          const set = (v: string | boolean) => setAnswers({ ...answers, [field.name]: v })
          return (
            <div className="field" key={field.name}>
              {field.type === 'Check' ? (
                <label><input type="checkbox" checked={value === true} onChange={(e) => set(e.target.checked)} /> {field.label}</label>
              ) : (
                <>
                  <label htmlFor={id}>{field.label}{field.required && <span aria-hidden="true"> *</span>}</label>
                  {field.type === 'Select' ? (
                    <Select id={id} value={(value as string) ?? ''} onChange={(e) => set(e.target.value)} aria-invalid={Boolean(error)}>
                      <option value="" />
                      {field.options.map((o) => <option key={o} value={o}>{o}</option>)}
                    </Select>
                  ) : isMultiline(field.type) ? (
                    <textarea id={id} rows={4} maxLength={5000} value={(value as string) ?? ''} onChange={(e) => set(e.target.value)} aria-invalid={Boolean(error)} />
                  ) : (
                    <input id={id} type={inputType(field.type)} maxLength={5000} value={(value as string) ?? ''} onChange={(e) => set(e.target.value)} aria-invalid={Boolean(error)} />
                  )}
                </>
              )}
              {field.description && <small className="muted">{field.description}</small>}
              {error && <small className="error-text" role="alert">{error}</small>}
            </div>
          )
        })}
        {/* A field people never see; a bot that fills in everything gives itself away. */}
        <div aria-hidden="true" style={{ position: 'absolute', insetInlineStart: '-10000px' }}>
          <label>Leave this empty<input tabIndex={-1} autoComplete="off" value={trap} onChange={(e) => setTrap(e.target.value)} /></label>
        </div>
        <button className="btn primary" type="submit" disabled={send.isPending}>{send.isPending ? 'Sending…' : 'Send'}</button>
      </form>
    </main>
  )
}
