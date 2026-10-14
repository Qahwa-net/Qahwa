import { useId, useState } from 'react'
import { localExpressionError } from '../lib/expr/check.ts'
import { FUNCTION_HELP, OPERATOR_HELP } from '../lib/expr/help.ts'

interface Props {
  label: string
  value: string
  onChange: (value: string) => void
  /** Field names the expression may read. */
  names: string[]
  allowParent?: boolean
  placeholder?: string
  hint?: string
  /** A problem reported by the server (shown when nothing is wrong locally). */
  serverError?: string
  disabled?: boolean
  help?: boolean
}

/** A single-line expression editor that checks the text as you type and can show what is available. */
export function ExpressionInput({ label, value, onChange, names, allowParent = false, placeholder, hint, serverError, disabled, help = true }: Props) {
  const id = useId()
  const [open, setOpen] = useState(false)
  const error = localExpressionError(value, names, allowParent) ?? serverError

  return (
    <div className="field full expression">
      <label htmlFor={id}>{label}</label>
      <input id={id} className="mono" value={value} placeholder={placeholder} disabled={disabled} spellCheck={false}
        autoComplete="off" aria-invalid={Boolean(error)} onChange={(e) => onChange(e.target.value)} />
      {hint && !error && <small className="muted">{hint}</small>}
      {error && <small className="error-text" role="alert">{error}</small>}
      {help && (
        <>
          <button type="button" className="link-button" aria-expanded={open} onClick={() => setOpen(!open)}>
            {open ? 'Hide help' : 'Fields and functions'}
          </button>
          {open && (
            <div className="expr-help">
              <p className="muted">{OPERATOR_HELP}</p>
              <p><strong>Fields:</strong>{' '}
                {names.map((n) => (
                  <button key={n} type="button" className="chip-button" disabled={disabled}
                    onClick={() => onChange(`${value}${value && !/[\s(]$/.test(value) ? ' ' : ''}${n}`)}>{n}</button>
                ))}
              </p>
              <dl>
                {FUNCTION_HELP.map((f) => (
                  <div key={f.name}>
                    <dt><code>{f.signature}</code></dt>
                    <dd>{f.description} <button type="button" className="link-button" disabled={disabled}
                      onClick={() => onChange(f.example)}>Try: <code>{f.example}</code></button></dd>
                  </div>
                ))}
              </dl>
            </div>
          )}
        </>
      )}
    </div>
  )
}
