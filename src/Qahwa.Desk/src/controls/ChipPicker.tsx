import { api } from '../api/client.ts'
import { Avatar, Icon } from './Icon.tsx'
import { LinkSelect } from './LinkSelect.tsx'
import { addUser } from '../lib/assignmentRules.ts'

/** A list of documents of one DocType (people, roles) shown as removable chips, with a search box to add more. */
export function ChipPicker({ doctype, values, onChange, label, placeholder, readOnly, avatars = false }: {
  doctype: string
  values: string[]
  onChange: (values: string[]) => void
  label: string
  placeholder: string
  readOnly?: boolean
  avatars?: boolean
}) {
  return (
    <>
      <ul className="chip-list" aria-label={label}>
        {values.map((v) => (
          <li key={v} className="person-chip">
            {avatars && <Avatar name={v} size={22} />} <span>{v}</span>
            {!readOnly && (
              <button type="button" className="icon-btn" aria-label={`Remove ${v}`} onClick={() => onChange(values.filter((x) => x !== v))}>
                <Icon name="x" size={14} />
              </button>
            )}
          </li>
        ))}
      </ul>
      {!readOnly && (
        <LinkSelect ariaLabel={`${label}: add`} placeholder={placeholder} value="" cacheKey={['chip-picker', doctype]}
          search={(text) => api.linkSearch(doctype, text)} onChange={(value) => { if (value) onChange(addUser(values, value)) }} />
      )}
    </>
  )
}
