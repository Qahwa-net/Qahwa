import type {
  DesignerDetail, DocPerm, DesignerItem, DesignerPreview, Doc, DocTypeDefinition, FieldError, FieldTypeInfo,
  ListResponse, MetaResponse, MetaSummary, PermissionItem, PermissionView, SessionUser,
} from './types.ts'
import type { Filter } from '../lib/filters.ts'
import { toApiFilters } from '../lib/filters.ts'

export class ApiError extends Error {
  status: number
  code: string
  details: FieldError[]

  constructor(status: number, code: string, message: string, details: FieldError[] = []) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.details = details
  }
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const response = await fetch(url, {
    method,
    credentials: 'same-origin',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })

  const text = await response.text()
  let json: unknown = null
  try {
    json = text ? JSON.parse(text) : null
  } catch {
    // Non-JSON error pages (proxy errors, etc.) fall through to the generic error below.
  }

  if (!response.ok) {
    const envelope = (json ?? {}) as { code?: string; message?: string; details?: FieldError[] }
    throw new ApiError(
      response.status,
      envelope.code ?? 'error',
      envelope.message ?? (response.statusText || `Request failed (${response.status})`),
      envelope.details ?? [],
    )
  }

  return json as T
}

const resource = (doctype: string, name?: string) =>
  `/api/resource/${encodeURIComponent(doctype)}${name === undefined ? '' : `/${encodeURIComponent(name)}`}`

export interface ListParams {
  fields: string[]
  filters: Filter[]
  orderBy: string
  descending: boolean
  limit: number
  offset: number
}

export const api = {
  /** Returns null when nobody is signed in. */
  async me(): Promise<SessionUser | null> {
    try {
      return (await request<{ data: SessionUser }>('GET', '/api/auth/me')).data
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) return null
      throw error
    }
  },

  async login(email: string, password: string): Promise<SessionUser> {
    return (await request<{ data: SessionUser }>('POST', '/api/auth/login', { email, password })).data
  },

  logout: () => request<unknown>('POST', '/api/auth/logout'),

  changePassword: (oldPassword: string, newPassword: string) =>
    request<unknown>('POST', '/api/auth/change-password', { old_password: oldPassword, new_password: newPassword }),

  async metas(): Promise<MetaSummary[]> {
    return (await request<{ data: MetaSummary[] }>('GET', '/api/meta')).data
  },

  meta: (doctype: string) => request<MetaResponse>('GET', `/api/meta/${encodeURIComponent(doctype)}`),

  list(doctype: string, params: ListParams): Promise<ListResponse> {
    const query = new URLSearchParams({
      fields: JSON.stringify(params.fields),
      order_by: `${params.orderBy} ${params.descending ? 'desc' : 'asc'}`,
      limit_page_length: String(params.limit),
      limit_start: String(params.offset),
    })
    if (params.filters.length > 0) query.set('filters', JSON.stringify(toApiFilters(params.filters)))
    return request<ListResponse>('GET', `${resource(doctype)}?${query}`)
  },

  async get(doctype: string, name: string): Promise<Doc> {
    return (await request<{ data: Doc }>('GET', resource(doctype, name))).data
  },

  async create(doctype: string, values: Record<string, unknown>): Promise<Doc> {
    return (await request<{ data: Doc }>('POST', resource(doctype), values)).data
  },

  async update(doctype: string, name: string, values: Record<string, unknown>): Promise<Doc> {
    return (await request<{ data: Doc }>('PUT', resource(doctype, name), values)).data
  },

  remove: (doctype: string, name: string) => request<unknown>('DELETE', resource(doctype, name)),

  permissions: {
    list: async () => (await request<{ data: PermissionItem[] }>('GET', '/api/permissions/doctypes')).data,
    get: async (name: string) =>
      (await request<{ data: PermissionView }>('GET', `/api/permissions/doctypes/${encodeURIComponent(name)}`)).data,
    save: async (name: string, rules: DocPerm[], modified: string | null | undefined) =>
      (await request<{ data: PermissionView }>('PUT', `/api/permissions/doctypes/${encodeURIComponent(name)}`, { rules, modified })).data,
    reset: async (name: string) =>
      (await request<{ data: PermissionView }>('DELETE', `/api/permissions/doctypes/${encodeURIComponent(name)}`)).data,
  },

  designer: {
    fieldTypes: async () => (await request<{ data: FieldTypeInfo[] }>('GET', '/api/designer/field-types')).data,
    list: async () => (await request<{ data: DesignerItem[] }>('GET', '/api/designer/doctypes')).data,
    get: async (name: string) =>
      (await request<{ data: DesignerDetail }>('GET', `/api/designer/doctypes/${encodeURIComponent(name)}`)).data,
    preview: async (definition: DocTypeDefinition, existing: string | null) =>
      (await request<{ data: DesignerPreview }>('POST', '/api/designer/preview', { definition, existing })).data,
    create: async (definition: DocTypeDefinition) =>
      (await request<{ data: DesignerDetail }>('POST', '/api/designer/doctypes', { definition })).data,
    update: async (name: string, definition: DocTypeDefinition, modified: string | null | undefined) =>
      (await request<{ data: DesignerDetail }>(
        'PUT', `/api/designer/doctypes/${encodeURIComponent(name)}`, { definition, modified })).data,
    remove: (name: string) => request<unknown>('DELETE', `/api/designer/doctypes/${encodeURIComponent(name)}`),
  },
}
