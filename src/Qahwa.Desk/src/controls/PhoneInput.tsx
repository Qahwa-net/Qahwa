import { SearchSelect } from './SearchSelect.tsx'
import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client.ts'
import { dialCountries, joinPhone, splitPhone, type DialCountry } from '../lib/phone.ts'

interface Props {
  id?: string
  ariaLabel?: string
  value: string
  readOnly?: boolean
  invalid?: boolean
  onChange: (value: string) => void
}

/** A phone number with a country picker: the dialling code comes from the Country list; the stored value is "+966 50 123 4567". */
export function PhoneInput({ id, ariaLabel, value, readOnly, invalid, onChange }: Props) {
  const list = useQuery({
    queryKey: ['dial-countries'], staleTime: Infinity, retry: false,
    queryFn: async () => dialCountries((await api.list('Country', { fields: ['name', 'dial_code'], filters: [], orderBy: 'country_name', descending: false, limit: 300, offset: 0 })).data),
  })
  const countries = list.data ?? []
  const parsed = splitPhone(value, countries)
  // Several countries share a code (+1): remember which one was picked so the box does not jump to another.
  const [picked, setPicked] = useState<DialCountry | null>(null)
  useEffect(() => { if (picked && parsed.country?.dial !== picked.dial) setPicked(null) }, [picked, parsed.country?.dial])
  const country = picked && parsed.country?.dial === picked.dial ? picked : parsed.country

  return (
    <span className="phone-input">
      {countries.length > 0 && (
        <SearchSelect label="Country code" value={country?.name ?? ''} readOnly={readOnly} placeholder="No code"
          options={countries.map(c => ({ value: c.name, label: c.name + ' (+' + c.dial + ')' }))}
          onChange={value => {
            const next = countries.find(c => c.name === value) ?? null
            setPicked(next)
            onChange(joinPhone(next, parsed.rest))
          }} />
      )}
      <input id={id} type="tel" inputMode="tel" autoComplete="tel-national" maxLength={25} aria-label={ariaLabel} aria-invalid={invalid} readOnly={readOnly}
        value={parsed.rest} onChange={(e) => onChange(joinPhone(country, e.target.value))} />
    </span>
  )
}
