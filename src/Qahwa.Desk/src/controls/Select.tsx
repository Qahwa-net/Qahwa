import { Children, Fragment, isValidElement, type ReactElement, type ReactNode } from 'react'
import type { LinkOption } from '../api/types.ts'
import { LinkSelect } from './LinkSelect.tsx'

interface Props {
  value?: string | number
  onChange?: (e: { target: { value: string; label: string } }) => void
  disabled?: boolean
  id?: string
  className?: string
  title?: string
  required?: boolean
  children?: ReactNode
  'aria-label'?: string
  'aria-invalid'?: boolean | 'true' | 'false'
}

function text(node: ReactNode): string {
  if (node == null || typeof node === 'boolean') return ''
  if (typeof node === 'string' || typeof node === 'number') return String(node)
  if (Array.isArray(node)) return node.map(text).join('')
  if (isValidElement(node)) return text((node.props as { children?: ReactNode }).children)
  return ''
}

/** Reads the `<option>` elements (also inside fragments, arrays and `<optgroup>`) into choices. */
function optionsOf(children: ReactNode, into: LinkOption[] = []): LinkOption[] {
  Children.forEach(children, (child) => {
    if (!isValidElement(child)) return
    const el = child as ReactElement<{ value?: string | number; children?: ReactNode; disabled?: boolean }>
    if (el.type === 'option') {
      if (el.props.disabled) return
      const label = text(el.props.children)
      into.push({ value: String(el.props.value ?? label), label })
    } else if (el.type === Fragment || el.type === 'optgroup') optionsOf(el.props.children, into)
  })
  return into
}

/**
 * A drop-in replacement for `<select>` with the same `<option>` children and `onChange(e.target.value)`,
 * but searchable: click, type to narrow the list, pick with the mouse or the arrow keys.
 */
export function Select({ value = '', onChange, disabled, id, className, title, children, 'aria-label': ariaLabel, 'aria-invalid': invalid }: Props) {
  const all = optionsOf(children)
  const current = String(value)
  const blank = all.find((o) => o.value === '')
  // The empty option (if any) is the "nothing chosen" state; it is not a pickable row with no text.
  const options = all.filter((o) => o.value !== '' || o.label !== '')
  const known = all.find((o) => o.value === current)
  return (
    <span className={`select-wrap${className ? ` ${className}` : ''}`} title={title}>
      <LinkSelect
        id={id} ariaLabel={ariaLabel} value={current} readOnly={disabled} invalid={invalid === true || invalid === 'true'}
        onChange={(v, o) => onChange?.({ target: { value: v, label: o?.label ?? '' } })}
        clearable={Boolean(blank)} cacheKey={['select', id ?? ariaLabel, options]} search={async () => []}
        localOptions={options} knownLabel={known?.label ?? (current === '' ? '' : undefined)}
        placeholder={blank?.label || 'Select…'}
      />
    </span>
  )
}
