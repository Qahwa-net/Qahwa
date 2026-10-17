import type { LinkOption } from '../api/types.ts'
import { LinkSelect } from './LinkSelect.tsx'
export function SearchSelect({ id, label, value, options, onChange, readOnly, invalid, placeholder = 'Select…' }: { id?: string; label?: string; value: string; options: LinkOption[]; onChange: (value: string) => void; readOnly?: boolean; invalid?: boolean; placeholder?: string }) {
  return <LinkSelect id={id} ariaLabel={label} value={value} readOnly={readOnly} invalid={invalid} onChange={onChange} cacheKey={['select', id ?? label, options]} search={async () => []} localOptions={options} knownLabel={options.find(o => o.value === value)?.label} placeholder={placeholder} />
}
