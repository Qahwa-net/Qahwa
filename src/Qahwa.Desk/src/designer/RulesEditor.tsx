import type { ValidationRule } from '../api/types.ts'
import { ExpressionInput } from './ExpressionInput.tsx'
import { Select } from '../controls/Select.tsx'

interface Props {
  rules: ValidationRule[]
  /** Fields the rules may read, and the ones a message may be placed under. */
  fieldNames: string[]
  fieldLabels: Record<string, string>
  allowParent: boolean
  errors: Record<string, string>
  disabled: boolean
  onChange: (rules: ValidationRule[]) => void
}

/** Rules a document (or a table row) must satisfy before it can be saved. */
export function RulesEditor({ rules, fieldNames, fieldLabels, allowParent, errors, disabled, onChange }: Props) {
  const update = (index: number, patch: Partial<ValidationRule>) =>
    onChange(rules.map((r, i) => (i === index ? { ...r, ...patch } : r)))

  return (
    <div>
      {rules.length === 0 && <p className="muted">No rules yet. For example: <code>qty &gt; 0</code> with the message “Quantity must be positive”.</p>}
      <ul className="rule-list">
        {rules.map((rule, i) => {
          const path = `validations[${i}]`
          return (
            <li key={i} className="rule">
              <ExpressionInput label="The rule holds when" value={rule.expression} names={fieldNames} allowParent={allowParent}
                disabled={disabled} placeholder="end_date >= start_date" serverError={errors[`${path}.expression`]}
                onChange={(expression) => update(i, { expression })} help={i === 0} />
              <label className="field">
                <span>Otherwise say</span>
                <input value={rule.message} disabled={disabled} onChange={(e) => update(i, { message: e.target.value })}
                  aria-invalid={Boolean(errors[`${path}.message`])} placeholder="The end date cannot be before the start date" />
                {errors[`${path}.message`] && <small className="error-text" role="alert">{errors[`${path}.message`]}</small>}
              </label>
              <label className="field">
                <span>Show the message under</span>
                <Select value={rule.field ?? ''} disabled={disabled} onChange={(e) => update(i, { field: e.target.value || null })}>
                  <option value="">The whole {allowParent ? 'row' : 'document'}</option>
                  {fieldNames.map((n) => <option key={n} value={n}>{fieldLabels[n] || n}</option>)}
                </Select>
                {errors[`${path}.field`] && <small className="error-text" role="alert">{errors[`${path}.field`]}</small>}
              </label>
              <button type="button" className="btn small icon danger" aria-label={`Remove rule ${i + 1}`} disabled={disabled}
                onClick={() => onChange(rules.filter((_, n) => n !== i))}>✕</button>
            </li>
          )
        })}
      </ul>
      {errors.validations && <small className="error-text" role="alert">{errors.validations}</small>}
      <button type="button" className="btn small btn-gap" disabled={disabled}
        onClick={() => onChange([...rules, { expression: '', message: '', field: null }])}>+ Add rule</button>
    </div>
  )
}
