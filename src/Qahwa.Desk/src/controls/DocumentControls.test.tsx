// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SearchSelect } from './SearchSelect.tsx'
import { FieldInput } from './FieldControl.tsx'
import { FilePreview } from './FilePreview.tsx'
import { AuthProvider } from '../auth.tsx'
import { api } from '../api/client.ts'
import type { DocField, MetaResponse } from '../api/types.ts'

Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
let container: HTMLDivElement, root: Root
const selectField: DocField = { name: 'customer', type: 'Link', options: 'Customer', label: 'Customer', required: false, unique: false, inListView: false, lowercase: false }
const targetMeta: MetaResponse = { data: { name: 'Customer', isChildTable: false, isSubmittable: false, trackChanges: true, titleField: 'customer_name', autoname: 'CUST-.#####', fields: [{ ...selectField, name: 'customer_name', label: 'Customer Name', type: 'Data', options: undefined, required: true }], permissions: [] }, permissions: { read: true, write: true, create: true, delete: false, export: false, report: false }, children: {} }
async function waitFor(check: () => void) {
  let error: unknown
  for (let i = 0; i < 35; i++) {
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 15)) })
    try { check(); return } catch (e) { error = e }
  }
  throw error
}
async function inputValue(input: HTMLInputElement, value: string) {
  await act(async () => { Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, value); input.dispatchEvent(new Event('input', { bubbles: true })) })
}
function wrapper(node: React.ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return <QueryClientProvider client={client}><AuthProvider>{node}</AuthProvider></QueryClientProvider>
}
beforeEach(() => {
  container = document.createElement('div'); document.body.append(container); root = createRoot(container)
  vi.spyOn(api, 'me').mockResolvedValue({ user: 'ann@example.com', full_name: 'Ann', roles: ['Employee'] })
})
afterEach(async () => { await act(async () => root.unmount()); container.remove(); vi.restoreAllMocks() })

describe('Searchable document controls', () => {
  it('filters static options immediately and selects without submitting a form', async () => {
    const change = vi.fn(), submitted = vi.fn()
    await act(async () => root.render(wrapper(<form onSubmit={submitted}><SearchSelect label="Status" value="" options={[{ value: 'Draft', label: 'Draft' }, { value: 'Approved', label: 'Approved' }]} onChange={change} /></form>)))
    const input = document.querySelector('input[role="combobox"]') as HTMLInputElement
    await act(async () => input.focus())
    await inputValue(input, 'appro')
    expect(document.querySelector('[role="listbox"]')?.textContent).toContain('Approved')
    expect(document.querySelector('[role="listbox"]')?.textContent).not.toContain('Draft')
    await act(async () => document.querySelector('[role="option"]')!.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true })))
    expect(change).toHaveBeenCalledWith('Approved', { value: 'Approved', label: 'Approved' })
    expect(submitted).not.toHaveBeenCalled()
  })
  it('does not offer creation without create permission', async () => {
    vi.spyOn(api, 'meta').mockResolvedValue({ ...targetMeta, permissions: { ...targetMeta.permissions, create: false } })
    vi.spyOn(api, 'linkSearch').mockResolvedValue([])
    await act(async () => root.render(wrapper(<FieldInput field={selectField} value="" onChange={vi.fn()} />)))
    await act(async () => (document.querySelector('[role="combobox"]') as HTMLInputElement).focus())
    await waitFor(() => expect(document.querySelector('[role="listbox"]')?.textContent).toContain('Nothing found'))
    expect(document.querySelector('.link-create')).toBeNull()
  })
  it('creates and selects a linked document while preserving the parent form', async () => {
    vi.spyOn(api, 'meta').mockResolvedValue(targetMeta)
    vi.spyOn(api, 'linkSearch').mockResolvedValue([])
    const create = vi.spyOn(api, 'create').mockResolvedValue({ name: 'CUST-00001', customer_name: 'Acme' })
    const change = vi.fn(), parentSubmit = vi.fn()
    await act(async () => root.render(wrapper(<form onSubmit={parentSubmit}><input aria-label="Parent title" defaultValue="Unsaved parent" /><FieldInput field={selectField} value="" onChange={change} /></form>)))
    await act(async () => (document.querySelector('[role="combobox"]') as HTMLInputElement).focus())
    await waitFor(() => expect(document.querySelector('.link-create')).not.toBeNull())
    await act(async () => document.querySelector('.link-create')!.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true })))
    await waitFor(() => expect(document.querySelector('[role="dialog"] input')).not.toBeNull())
    await inputValue(document.querySelector('[role="dialog"] input') as HTMLInputElement, 'Acme')
    await act(async () => document.querySelector('[role="dialog"] form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))
    await waitFor(() => expect(change).toHaveBeenCalledWith('CUST-00001'))
    expect(create).toHaveBeenCalledWith('Customer', { customer_name: 'Acme' })
    expect((document.querySelector('[aria-label="Parent title"]') as HTMLInputElement).value).toBe('Unsaved parent')
    expect(parentSubmit).not.toHaveBeenCalled()
  })
  it('opens raster attachments in a viewer without a download link', async () => {
    await act(async () => root.render(wrapper(<FilePreview file={{ id: 'image', fileName: 'photo.png', contentType: 'image/png', image: true, size: 100, uploadedBy: 'Ann', created: '' }} onClose={vi.fn()} />)))
    expect(document.querySelector('[role="dialog"] img')?.getAttribute('src')).toBe('/api/files/image')
    expect(document.querySelector('[role="dialog"] a[download]')).toBeNull()
  })
})
