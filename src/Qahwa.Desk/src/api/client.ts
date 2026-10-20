import type { DeskLayout, DeskRoleLayout, DeskState } from '../lib/desk.ts'
import type { ScriptSource } from '../lib/clientScripts.ts'
import type { EmailedReport, EmailedReportRequest } from '../lib/emailedReports.ts'
import type { SearchResponse } from '../lib/search.ts'
import type { ImportSource } from '../lib/importFile.ts'
import type { SsoProviderInfo } from '../lib/sso.ts'
import type { DisplaySettings } from '../lib/display.ts'
import type {
  DesignerDetail, DocPerm, DesignerItem, DesignerPreview, Doc, DocTypeDefinition, FieldError, FieldTypeInfo,
  ListResponse, ReportDetail, ReportInfo, ReportRun, RestoreReport, SignIn, ExpiredPasswordChallenge, TwoFactorChallenge, TwoFactorSetup, TwoFactorStatus, PublicForm, BundleReport, TranslationCatalog, AttachedFile, WebhookItem, WebhookSet, WebhookView, ApiKeyCreated, ApiKeyInfo, ImportPreview, ImportResult, WorkspaceBlock, WorkspaceDetail, WorkspaceInfo, WorkspaceView, LetterHeadDetail, PrintChoices, PrintFormatDetail, PrintFormatInfo, DashboardDetail, DashboardInfo, DashboardItem, DashboardResult, EmailPreview, EmailTemplateInfo, NotificationRuleItem, NotificationRuleSet, NotificationRuleView, AutoRepeatInfo, AutoRepeatRequest, AssignmentRuleItem, AssignmentRuleSet, AssignmentRuleView, PendingAction, WorkflowDefinition, WorkflowItem, WorkflowStatus, WorkflowView, MetaResponse, MetaSummary, PermissionItem, ActivityItem, AssignmentInfo, Customization, CustomizationItem, CustomizationView, BulkResult, AppInfo, OAuthAuthorizeParams, OAuthConsent, OAuthClientInfo, OAuthConnected, EmailDigest, ScriptReportInfo, ScriptReportResult, Viewer, FollowState, LikeState, TagCount, GroupCountItem, LinkOption, ListViewSettings, ListViewState, SavedFilter, NotificationItem, PermissionView, ReportRow, SeriesInfo, SessionUser, ShareInfo, TreeNode,
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
  desk: {
    get: async (): Promise<DeskState> => (await request<{ data: DeskState }>('GET', '/api/desk-settings')).data,
    save: async (layout: DeskLayout, modified: string | null): Promise<DeskState> => (await request<{ data: DeskState }>('PUT', '/api/desk-settings', { layout, modified })).data,
    reset: async (): Promise<DeskState> => (await request<{ data: DeskState }>('DELETE', '/api/desk-settings')).data,
    role: async (role: string): Promise<DeskRoleLayout> => (await request<{ data: DeskRoleLayout }>('GET', '/api/desk-settings/roles/' + encodeURIComponent(role))).data,
    saveRole: async (role: string, layout: DeskLayout, modified: string | null): Promise<DeskRoleLayout> => (await request<{ data: DeskRoleLayout }>('PUT', '/api/desk-settings/roles/' + encodeURIComponent(role), { layout, modified })).data,
    resetRole: (role: string) => request('DELETE', '/api/desk-settings/roles/' + encodeURIComponent(role)),
  },
  userSecurity: {
    setPassword: (email: string, newPassword: string) => request<{ message: string }>('POST', `/api/users/${encodeURIComponent(email)}/password`, { newPassword }),
    sendReset: (email: string) => request<{ message: string }>('POST', `/api/users/${encodeURIComponent(email)}/reset-password`, {}),
    testEmail: (to: string) => request<{ message: string }>('POST', '/api/email/test', { to }),
    testIncomingEmail: () => request<{ message: string }>('POST', '/api/email/test-incoming'),
  },
  /** Returns null when nobody is signed in. */
  async me(): Promise<SessionUser | null> {
    try {
      return (await request<{ data: SessionUser }>('GET', '/api/auth/me')).data
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) return null
      throw error
    }
  },

  async login(email: string, password: string): Promise<SessionUser | TwoFactorChallenge | ExpiredPasswordChallenge> {
    return (await request<{ data: SessionUser | TwoFactorChallenge | ExpiredPasswordChallenge }>('POST', '/api/auth/login', { email, password })).data
  },

  changeExpiredPassword: (token: string, oldPassword: string, newPassword: string) =>
    request<unknown>('POST', '/api/auth/expired-password', { token, old_password: oldPassword, new_password: newPassword }),

  async loginWithCode(token: string, code: string): Promise<SessionUser> {
    return (await request<{ data: SessionUser }>('POST', '/api/auth/login/2fa', { token, code })).data
  },

  reports: {
    list: async () => (await request<{ data: ReportInfo[] }>('GET', '/api/reports')).data,
    get: async (id: string) => (await request<{ data: ReportDetail }>('GET', `/api/reports/${encodeURIComponent(id)}`)).data,
    create: async (body: { title: string; isPublic: boolean; definition: Record<string, unknown> }) => (await request<{ data: ReportInfo }>('POST', '/api/reports', body)).data,
    update: async (id: string, body: { title: string; isPublic: boolean; definition: Record<string, unknown> }, modified: string) =>
      (await request<{ data: ReportInfo }>('PUT', `/api/reports/${encodeURIComponent(id)}?modified=${encodeURIComponent(modified)}`, body)).data,
    remove: (id: string) => request<unknown>('DELETE', `/api/reports/${encodeURIComponent(id)}`),
    run: async (id: string) => (await request<{ data: ReportRun }>('GET', `/api/reports/${encodeURIComponent(id)}/run`)).data,
    preview: async (definition: Record<string, unknown>) => (await request<{ data: ReportRun }>('POST', '/api/reports/preview', definition)).data,
    csvUrl: (id: string) => `/api/reports/${encodeURIComponent(id)}/csv`,
    xlsxUrl: (id: string) => `/api/reports/${encodeURIComponent(id)}/xlsx`,
  },

  backup: {
    /** The file is sent as it is (application/zip), so it never has to be read into the page. */
    restore: async (file: File, dryRun: boolean): Promise<RestoreReport> => {
      const response = await fetch(`/api/backup/restore?dryRun=${dryRun}`, { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/zip' }, body: file })
      const json = (await response.json().catch(() => null)) as { data?: RestoreReport; code?: string; message?: string; details?: FieldError[] } | null
      if (!response.ok) throw new ApiError(response.status, json?.code ?? 'error', json?.message ?? response.statusText, json?.details ?? [])
      return json!.data!
    },
  },

  clientScripts: async (doctype: string): Promise<ScriptSource[]> =>
    (await request<{ data: { name: string; title: string; source: string }[] }>('GET', `/api/client-scripts/${encodeURIComponent(doctype)}`)).data,

  search: async (q: string): Promise<SearchResponse> => (await request<{ data: SearchResponse }>('GET', `/api/search?q=${encodeURIComponent(q)}`)).data,

  brand: async (): Promise<unknown> => (await request<{ data: unknown }>('GET', '/api/brand')).data,

  settings: async () => (await request<{ data: DisplaySettings }>('GET', '/api/settings')).data,

  signUp: {
    enabled: async () => (await request<{ data: { enabled: boolean } }>('GET', '/api/auth/signup')).data.enabled,
    ask: (email: string, fullName: string) => request<{ message: string }>('POST', '/api/auth/signup', { email, full_name: fullName }),
    confirm: (token: string, password: string) => request<unknown>('POST', '/api/auth/signup/confirm', { token, password }),
  },

  passwordReset: {
    ask: (email: string) => request<{ message: string }>('POST', '/api/auth/forgot', { email }),
    reset: (token: string, newPassword: string) => request<unknown>('POST', '/api/auth/reset-password', { token, new_password: newPassword }),
  },

  sso: {
    providers: async () => (await request<{ data: SsoProviderInfo[] }>('GET', '/api/auth/sso')).data,
    samlProviders: async () => (await request<{ data: SsoProviderInfo[] }>('GET', '/api/auth/saml')).data,
  },
  sessions: {
    list: async () => (await request<{ data: SignIn[] }>('GET', '/api/auth/sessions')).data,
    end: (id: string) => request<unknown>('DELETE', `/api/auth/sessions/${encodeURIComponent(id)}`),
    endOthers: async () => (await request<{ data: { revoked: number } }>('POST', '/api/auth/sessions/revoke-others')).data.revoked,
    endUser: (user: string) => request<unknown>('POST', '/api/auth/sessions/revoke-user', { user }),
  },

  twoFactor: {
    status: async () => (await request<{ data: TwoFactorStatus }>('GET', '/api/auth/2fa')).data,
    setup: async () => (await request<{ data: TwoFactorSetup }>('POST', '/api/auth/2fa/setup')).data,
    enable: async (code: string) => (await request<{ data: { recovery_codes: string[] } }>('POST', '/api/auth/2fa/enable', { code })).data.recovery_codes,
    disable: (password: string, code: string) => request<unknown>('POST', '/api/auth/2fa/disable', { password, code }),
    reset: (user: string) => request<unknown>('POST', '/api/auth/2fa/reset', { user }),
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

  /** Type-ahead choices for a Link field: documents of the target DocType matching the text, by name or title. */
  async linkSearch(doctype: string, text: string): Promise<LinkOption[]> {
    const query = new URLSearchParams({ q: text, limit: '10' })
    return (await request<{ data: LinkOption[] }>('GET', `/api/link/${encodeURIComponent(doctype)}/search?${query}`)).data
  },

  /** The friendly names of documents already chosen, keyed by their stored name. */
  async linkTitles(doctype: string, names: string[]): Promise<Record<string, LinkOption>> {
    if (names.length === 0) return {}
    const query = new URLSearchParams({ names: JSON.stringify(names) })
    return (await request<{ data: Record<string, LinkOption> }>('GET', `/api/link/${encodeURIComponent(doctype)}/titles?${query}`)).data
  },

  /** Puts a deleted document back (System Manager). Returns the name it has again. */
  async restoreDeleted(id: string): Promise<string> {
    return (await request<{ data: { name: string } }>('POST', `/api/deleted-documents/${encodeURIComponent(id)}/restore`)).data.name
  },

  /** Renames a document, or merges it into an existing one. Returns the new name. */
  async rename(doctype: string, name: string, newName: string, merge: boolean): Promise<string> {
    return (await request<{ data: { name: string } }>('POST', `${resource(doctype, name)}/rename`, { newName, merge })).data.name
  },

  async tree(doctype: string, parent: string | null): Promise<TreeNode[]> {
    const query = parent ? `?parent=${encodeURIComponent(parent)}` : ''
    return (await request<{ data: TreeNode[] }>('GET', `/api/tree/${encodeURIComponent(doctype)}${query}`)).data
  },

  /** A link the browser can follow to download the matching documents as CSV. */
  exportUrl(doctype: string, filters: Filter[], format: 'csv' | 'xlsx' = 'csv'): string {
    const query = new URLSearchParams()
    if (format === 'xlsx') query.set('format', 'xlsx')
    if (filters.length > 0) query.set('filters', JSON.stringify(toApiFilters(filters)))
    return `${resource(doctype)}/export${query.size > 0 ? `?${query}` : ''}`
  },

  async report(doctype: string, groupBy: string, aggregate: string, field: string | null, filters: Filter[]) {
    const query = new URLSearchParams({ group_by: groupBy, aggregate })
    if (field) query.set('field', field)
    if (filters.length > 0) query.set('filters', JSON.stringify(toApiFilters(filters)))
    return request<{ data: ReportRow[]; truncated: boolean }>('GET', `${resource(doctype)}/report?${query}`)
  },

  naming: {
    list: async () => (await request<{ data: SeriesInfo[] }>('GET', '/api/naming/series')).data,
    set: async (prefix: string, current: number) =>
      (await request<{ data: SeriesInfo[] }>('PUT', '/api/naming/series', { prefix, current })).data,
  },

  /** Submittable DocTypes: draft (0) to submitted (1) to cancelled (2). */
  async submit(doctype: string, name: string, modified: unknown): Promise<Doc> {
    return (await request<{ data: Doc }>('POST', `${resource(doctype, name)}/submit`, { modified })).data
  },

  async cancel(doctype: string, name: string, modified: unknown): Promise<Doc> {
    return (await request<{ data: Doc }>('POST', `${resource(doctype, name)}/cancel`, { modified })).data
  },

  async amend(doctype: string, name: string): Promise<Doc> {
    return (await request<{ data: Doc }>('POST', `${resource(doctype, name)}/amend`)).data
  },

  share: {
    users: async (doctype: string, name: string, text: string) =>
      (await request<{ data: LinkOption[] }>('GET', `/api/share/${encodeURIComponent(doctype)}/${encodeURIComponent(name)}/users?${new URLSearchParams({ q: text })}`)).data,
    list: async (doctype: string, name: string) =>
      (await request<{ data: ShareInfo[] }>('GET', `/api/share/${encodeURIComponent(doctype)}/${encodeURIComponent(name)}`)).data,
    set: async (doctype: string, name: string, user: string, read: boolean, write: boolean) =>
      (await request<{ data: ShareInfo[] }>('PUT', `/api/share/${encodeURIComponent(doctype)}/${encodeURIComponent(name)}`, { user, read, write })).data,
  },

  listSettings: {
    get: async (doctype: string) => (await request<{ data: ListViewState }>('GET', `/api/list-settings/${encodeURIComponent(doctype)}`)).data,
    save: async (doctype: string, settings: ListViewSettings) =>
      (await request<{ data: ListViewSettings }>('PUT', `/api/list-settings/${encodeURIComponent(doctype)}/settings`, settings)).data,
    reset: (doctype: string) => request<unknown>('DELETE', `/api/list-settings/${encodeURIComponent(doctype)}/settings`),
    saveFilter: async (doctype: string, name: string, filters: Filter[], isPublic: boolean) =>
      (await request<{ data: SavedFilter }>('POST', `/api/list-settings/${encodeURIComponent(doctype)}/filters`,
        { name, filters: filters.map((f) => [f.field, f.op, f.value]), isPublic })).data,
    deleteFilter: (doctype: string, id: string) =>
      request<unknown>('DELETE', `/api/list-settings/${encodeURIComponent(doctype)}/filters/${encodeURIComponent(id)}`),
  },

  /** How many documents have each value of a field, under the list's filters (for the sidebar). */
  async groupBy(doctype: string, field: string, filters: Filter[]): Promise<GroupCountItem[]> {
    const query = new URLSearchParams({ field, limit: '20' })
    if (filters.length > 0) query.set('filters', JSON.stringify(toApiFilters(filters)))
    return (await request<{ data: GroupCountItem[] }>('GET', `${resource(doctype)}/group-by?${query}`)).data
  },

  /** Comment counts for the documents on screen, keyed by name. */
  async commentCounts(doctype: string, names: string[]): Promise<Record<string, number>> {
    if (names.length === 0) return {}
    const query = new URLSearchParams({ names: JSON.stringify(names) })
    return (await request<{ data: Record<string, number> }>('GET', `${resource(doctype)}/comment-counts?${query}`)).data
  },

  /** Change one field on, or delete, several documents; each is reported on its own. */
  async bulk(doctype: string, body: { action: 'update' | 'delete' | 'rename' | 'tag'; names: string[]; field?: string; value?: unknown; renames?: { from: string; to: string }[]; merge?: boolean }) {
    return request<{ data: BulkResult[]; succeeded: number; failed: number }>('POST', `${resource(doctype)}/bulk`, body)
  },

  notifications: {
    list: async (unreadOnly: boolean) =>
      (await request<{ data: NotificationItem[] }>('GET', `/api/notifications?limit=30${unreadOnly ? '&unread=true' : ''}`)).data,
    unread: async () => (await request<{ data: { unread: number } }>('GET', '/api/notifications/count')).data.unread,
    read: (id: string) => request<unknown>('POST', `/api/notifications/${encodeURIComponent(id)}/read`),
    readAll: () => request<unknown>('POST', '/api/notifications/read-all'),
  },

  viewers: async (doctype: string, name: string) => (await request<{ data: Viewer[] }>('GET', `${resource(doctype, name)}/viewers`)).data,

  tags: {
    get: async (doctype: string, name: string) => (await request<{ data: string[] }>('GET', `${resource(doctype, name)}/tags`)).data,
    add: async (doctype: string, name: string, tag: string) => (await request<{ data: string[] }>('POST', `${resource(doctype, name)}/tags`, { tag })).data,
    remove: async (doctype: string, name: string, tag: string) =>
      (await request<{ data: string[] }>('DELETE', `${resource(doctype, name)}/tags/${encodeURIComponent(tag)}`)).data,
    popular: async (doctype: string) => (await request<{ data: TagCount[] }>('GET', `/api/tags/${encodeURIComponent(doctype)}`)).data,
  },
  like: {
    get: async (doctype: string, name: string) => (await request<{ data: LikeState }>('GET', `${resource(doctype, name)}/like`)).data,
    set: async (doctype: string, name: string) => (await request<{ data: LikeState }>('POST', `${resource(doctype, name)}/like`)).data,
    unset: async (doctype: string, name: string) => (await request<{ data: LikeState }>('DELETE', `${resource(doctype, name)}/like`)).data,
  },
  follow: {
    get: async (doctype: string, name: string) => (await request<{ data: FollowState }>('GET', `${resource(doctype, name)}/follow`)).data,
    set: async (doctype: string, name: string) => (await request<{ data: FollowState }>('POST', `${resource(doctype, name)}/follow`)).data,
    unset: async (doctype: string, name: string) => (await request<{ data: FollowState }>('DELETE', `${resource(doctype, name)}/follow`)).data,
  },

  assignments: {
    forDocument: async (doctype: string, name: string) =>
      (await request<{ data: AssignmentInfo[] }>('GET', `${resource(doctype, name)}/assignments`)).data,
    assign: async (doctype: string, name: string, user: string, description: string, date: string, priority: string) =>
      (await request<{ data: AssignmentInfo[] }>('POST', `${resource(doctype, name)}/assignments`, { user, description, date: date || null, priority })).data,
    mine: async () => (await request<{ data: AssignmentInfo[] }>('GET', '/api/assignments/mine')).data,
    complete: (id: string) => request<unknown>('POST', `/api/assignments/${encodeURIComponent(id)}/complete`),
    remove: (id: string) => request<unknown>('DELETE', `/api/assignments/${encodeURIComponent(id)}`),
  },

  activity: {
    timeline: async (doctype: string, name: string, before?: string) => {
      const query = new URLSearchParams({ limit: '30' })
      if (before) query.set('before', before)
      return (await request<{ data: ActivityItem[] }>('GET', `${resource(doctype, name)}/timeline?${query}`)).data
    },
    addComment: async (doctype: string, name: string, content: string) =>
      (await request<{ data: ActivityItem }>('POST', `${resource(doctype, name)}/comments`, { content })).data,
    updateComment: async (doctype: string, name: string, id: string, content: string) =>
      (await request<{ data: ActivityItem }>('PUT', `${resource(doctype, name)}/comments/${encodeURIComponent(id)}`, { content })).data,
    deleteComment: (doctype: string, name: string, id: string) =>
      request<unknown>('DELETE', `${resource(doctype, name)}/comments/${encodeURIComponent(id)}`),
  },

  customize: {
    list: async () => (await request<{ data: CustomizationItem[] }>('GET', '/api/customize/doctypes')).data,
    get: async (name: string) =>
      (await request<{ data: CustomizationView }>('GET', `/api/customize/doctypes/${encodeURIComponent(name)}`)).data,
    save: async (name: string, customization: Customization, modified: string | null | undefined) =>
      (await request<{ data: CustomizationView }>('PUT', `/api/customize/doctypes/${encodeURIComponent(name)}`, { customization, modified })).data,
    reset: async (name: string) =>
      (await request<{ data: CustomizationView }>('DELETE', `/api/customize/doctypes/${encodeURIComponent(name)}`)).data,
  },

  files: {
    list: async (doctype: string, name: string) =>
      (await request<{ data: AttachedFile[] }>('GET', `/api/files/for/${encodeURIComponent(doctype)}/${encodeURIComponent(name)}`)).data,
    url: (id: string) => `/api/files/${encodeURIComponent(id)}`,
    /** Sends the bytes as they are; the name travels in a header (percent-encoded). */
    async upload(doctype: string, name: string, file: File): Promise<AttachedFile> {
      const response = await fetch(`/api/files/for/${encodeURIComponent(doctype)}/${encodeURIComponent(name)}`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'X-File-Name': encodeURIComponent(file.name), 'Content-Type': 'application/octet-stream' },
        body: file,
      })
      const json = (await response.json().catch(() => null)) as { data?: AttachedFile; message?: string; errors?: { field: string; message: string }[] } | null
      if (!response.ok) throw new ApiError(response.status, 'upload_error', json?.message ?? 'Could not upload the file.', json?.errors ?? [])
      return json!.data!
    },
    remove: async (id: string) => { await request<unknown>('DELETE', `/api/files/${encodeURIComponent(id)}`) },
  },

  publicForms: {
    get: async (route: string) => (await request<{ data: PublicForm }>('GET', `/api/public/forms/${encodeURIComponent(route)}`)).data,
    submit: (route: string, payload: Record<string, unknown>) => request<{ message: string }>('POST', `/api/public/forms/${encodeURIComponent(route)}`, payload),
  },

  translations: {
    list: async () => (await request<{ data: string[] }>('GET', '/api/translations')).data,
    catalog: async (language: string) => (await request<{ data: TranslationCatalog }>('GET', `/api/translations/${encodeURIComponent(language)}`)).data,
  },

  bundle: {
    import: async (bundle: unknown, dryRun: boolean) => (await request<{ data: BundleReport }>('POST', `/api/bundle/import?dryRun=${dryRun}`, bundle)).data,
  },

  webhooks: {
    list: async () => (await request<{ data: WebhookItem[] }>('GET', '/api/webhooks/doctypes')).data,
    get: async (name: string) => (await request<{ data: WebhookView }>('GET', `/api/webhooks/doctypes/${encodeURIComponent(name)}`)).data,
    save: async (name: string, hooks: WebhookSet, modified: string | null | undefined) =>
      (await request<{ data: WebhookView }>('PUT', `/api/webhooks/doctypes/${encodeURIComponent(name)}`, { hooks, modified })).data,
    remove: async (name: string) => { await request<unknown>('DELETE', `/api/webhooks/doctypes/${encodeURIComponent(name)}`) },
  },

  apiKeys: {
    list: async () => (await request<{ data: ApiKeyInfo[] }>('GET', '/api/api-keys')).data,
    create: async (body: { description: string; readOnly: boolean; expiresOn?: string }) =>
      (await request<{ data: ApiKeyCreated }>('POST', '/api/api-keys', body)).data,
    revoke: async (id: string) => { await request<unknown>('DELETE', `/api/api-keys/${encodeURIComponent(id)}`) },
  },

  import: {
    templateUrl: (doctype: string, mode: string) => `/api/import/${encodeURIComponent(doctype)}/template?mode=${encodeURIComponent(mode)}`,
    preview: async (doctype: string, source: ImportSource, mode: string) =>
      (await request<{ data: ImportPreview }>('POST', `/api/import/${encodeURIComponent(doctype)}/preview`, { ...source, mode })).data,
    run: async (doctype: string, source: ImportSource, mode: string, stopOnError: boolean) =>
      (await request<{ data: ImportResult }>('POST', `/api/import/${encodeURIComponent(doctype)}`, { ...source, mode, stopOnError })).data,
  },

  workspaces: {
    mine: async () => (await request<{ data: WorkspaceView[] }>('GET', '/api/workspaces/mine')).data,
    list: async () => (await request<{ data: WorkspaceInfo[] }>('GET', '/api/workspaces')).data,
    get: async (id: string) => (await request<{ data: WorkspaceDetail }>('GET', `/api/workspaces/${encodeURIComponent(id)}`)).data,
    create: async (body: { title: string; sequence: number; roles: string[]; blocks: WorkspaceBlock[] }) =>
      (await request<{ data: WorkspaceDetail }>('POST', '/api/workspaces', body)).data,
    update: async (id: string, body: { title: string; sequence: number; roles: string[]; blocks: WorkspaceBlock[] }) =>
      (await request<{ data: WorkspaceDetail }>('PUT', `/api/workspaces/${encodeURIComponent(id)}`, body)).data,
    remove: async (id: string) => { await request<unknown>('DELETE', `/api/workspaces/${encodeURIComponent(id)}`) },
  },

  pdf: {
    status: async () => (await request<{ data: { available: boolean } }>('GET', '/api/pdf/status')).data.available,
    /** The address of the PDF file of a printout. */
    url: (doctype: string, name: string, format: string, letterHead: string) => {
      const query = new URLSearchParams()
      if (format) query.set('format', format)
      if (letterHead) query.set('letterhead', letterHead)
      return `/api/pdf/${encodeURIComponent(doctype)}/${encodeURIComponent(name)}${query.size ? `?${query}` : ''}`
    },
  },

  print: {
    choices: async (doctype: string) => (await request<{ data: PrintChoices }>('GET', `/api/print/choices?doctype=${encodeURIComponent(doctype)}`)).data,
    /** The URL of the printable page (opens in its own tab, which has a Print button). */
    url: (doctype: string, name: string, format: string, letterHead: string) => {
      const query = new URLSearchParams()
      if (format) query.set('format', format)
      if (letterHead) query.set('letterhead', letterHead)
      return `/api/print/${encodeURIComponent(doctype)}/${encodeURIComponent(name)}${query.size ? `?${query}` : ''}`
    },
    page: async (url: string) => {
      const response = await fetch(url, { credentials: 'same-origin' })
      if (!response.ok) throw new ApiError(response.status, 'print_error', response.status === 403 ? 'You may not print this.' : 'Could not load the printout.')
      return response.text()
    },
    formats: async () => (await request<{ data: PrintFormatInfo[] }>('GET', '/api/print-formats')).data,
    format: async (name: string) => (await request<{ data: PrintFormatDetail }>('GET', `/api/print-formats/${encodeURIComponent(name)}`)).data,
    saveFormat: async (body: { name: string; docType: string; html: string; isDefault: boolean; letterHead?: string | null }) =>
      (await request<{ data: PrintFormatDetail }>('PUT', '/api/print-formats', body)).data,
    removeFormat: async (name: string) => { await request<unknown>('DELETE', `/api/print-formats/${encodeURIComponent(name)}`) },
    preview: async (body: { docType: string; name: string; html: string; letterHead?: string | null }) =>
      (await request<{ data: { html: string } }>('POST', '/api/print-formats/preview', body)).data.html,
    letterHeads: async () => (await request<{ data: LetterHeadDetail[] }>('GET', '/api/letter-heads')).data,
    saveLetterHead: async (body: LetterHeadDetail) => (await request<{ data: LetterHeadDetail }>('PUT', '/api/letter-heads', body)).data,
    removeLetterHead: async (name: string) => { await request<unknown>('DELETE', `/api/letter-heads/${encodeURIComponent(name)}`) },
  },

  dashboards: {
    list: async () => (await request<{ data: DashboardInfo[] }>('GET', '/api/dashboards')).data,
    get: async (id: string) => (await request<{ data: DashboardDetail }>('GET', `/api/dashboards/${encodeURIComponent(id)}`)).data,
    data: async (id: string) => (await request<{ data: DashboardResult[] }>('GET', `/api/dashboards/${encodeURIComponent(id)}/data`)).data,
    create: async (body: { title: string; isPublic: boolean; items: DashboardItem[] }) =>
      (await request<{ data: DashboardInfo }>('POST', '/api/dashboards', body)).data,
    update: async (id: string, body: { title: string; isPublic: boolean; items: DashboardItem[]; modified?: string }) =>
      (await request<{ data: DashboardInfo }>('PUT', `/api/dashboards/${encodeURIComponent(id)}`, body)).data,
    remove: async (id: string) => { await request<unknown>('DELETE', `/api/dashboards/${encodeURIComponent(id)}`) },
    preview: async (item: DashboardItem) => (await request<{ data: DashboardResult }>('POST', '/api/dashboards/preview', item)).data,
  },

  email: {
    templates: async (doctype: string) =>
      (await request<{ data: EmailTemplateInfo[] }>('GET', `/api/email/templates?doctype=${encodeURIComponent(doctype)}`)).data,
    preview: async (doctype: string, name: string, template: string) =>
      (await request<{ data: EmailPreview }>('POST', `${resource(doctype, name)}/email/preview`, { template })).data,
    send: async (doctype: string, name: string, body: { to: string[]; subject: string; message: string; template?: string }) =>
      (await request<{ data: { queued: number } }>('POST', `${resource(doctype, name)}/email`, body)).data,
  },

  notificationRules: {
    list: async () => (await request<{ data: NotificationRuleItem[] }>('GET', '/api/notification-rules/doctypes')).data,
    get: async (name: string) =>
      (await request<{ data: NotificationRuleView }>('GET', `/api/notification-rules/doctypes/${encodeURIComponent(name)}`)).data,
    save: async (name: string, rules: NotificationRuleSet, modified: string | null | undefined) =>
      (await request<{ data: NotificationRuleView }>('PUT', `/api/notification-rules/doctypes/${encodeURIComponent(name)}`, { rules, modified })).data,
    remove: async (name: string) => { await request<unknown>('DELETE', `/api/notification-rules/doctypes/${encodeURIComponent(name)}`) },
  },

  scriptReports: {
    list: async () => (await request<{ data: ScriptReportInfo[] }>('GET', '/api/script-reports')).data,
    run: async (id: string, filters: Record<string, string>) =>
      (await request<{ data: ScriptReportResult }>('POST', `/api/script-reports/${encodeURIComponent(id)}/run`, { filters })).data,
  },

  oauth: {
    consent: async (p: OAuthAuthorizeParams) =>
      (await request<{ data: OAuthConsent }>('GET', `/api/oauth/authorize?client_id=${encodeURIComponent(p.clientId)}&redirect_uri=${encodeURIComponent(p.redirectUri)}&scope=${encodeURIComponent(p.scope)}&state=${encodeURIComponent(p.state)}&code_challenge=${encodeURIComponent(p.codeChallenge)}&code_challenge_method=${encodeURIComponent(p.codeChallengeMethod)}`)).data,
    decide: async (p: OAuthAuthorizeParams, approve: boolean) =>
      (await request<{ data: { redirect: string } }>('POST', '/api/oauth/authorize', { ...p, approve })).data.redirect,
    clients: async () => (await request<{ data: OAuthClientInfo[] }>('GET', '/api/oauth/clients')).data,
    createClient: async (body: { name: string; redirectUris: string; scopes: string[]; public: boolean }) =>
      (await request<{ data: { info: OAuthClientInfo; clientSecret: string | null } }>('POST', '/api/oauth/clients', body)).data,
    deleteClient: async (id: string) => { await request<unknown>('DELETE', `/api/oauth/clients/${encodeURIComponent(id)}`) },
    connected: async () => (await request<{ data: OAuthConnected[] }>('GET', '/api/oauth/connected')).data,
    disconnect: async (clientId: string) => { await request<unknown>('DELETE', `/api/oauth/connected/${encodeURIComponent(clientId)}`) },
  },

  apps: {
    list: async () => (await request<{ data: { apps: AppInfo[] } }>('GET', '/api/method')).data.apps,
    change: async (name: string, action: 'install' | 'uninstall') =>
      (await request<{ data: AppInfo[] }>('POST', `/api/apps/${encodeURIComponent(name)}/${action}`)).data,
  },

  boot: async () => (await request<{ data: { apps: Record<string, unknown> } }>('GET', '/api/boot')).data.apps,

  emailDigest: {
    get: async () => (await request<{ data: EmailDigest }>('GET', '/api/email-digest')).data,
    set: async (body: { enabled?: boolean; frequency?: string; sendHour?: number }) => (await request<{ data: EmailDigest }>('PUT', '/api/email-digest', body)).data,
  },

  emailedReports: {
    list: async () => (await request<{ data: EmailedReport[] }>('GET', '/api/auto-email-reports')).data,
    create: async (body: EmailedReportRequest) => (await request<{ data: EmailedReport }>('POST', '/api/auto-email-reports', body)).data,
    update: async (id: string, body: EmailedReportRequest) =>
      (await request<{ data: EmailedReport }>('PUT', `/api/auto-email-reports/${encodeURIComponent(id)}`, body)).data,
    send: async (id: string) =>
      (await request<{ data: { queued: number } }>('POST', `/api/auto-email-reports/${encodeURIComponent(id)}/send`)).data.queued,
    remove: async (id: string) => { await request<unknown>('DELETE', `/api/auto-email-reports/${encodeURIComponent(id)}`) },
  },

  autoRepeat: {
    mine: async () => (await request<{ data: AutoRepeatInfo[] }>('GET', '/api/auto-repeat')).data,
    forDocument: async (doctype: string, name: string) =>
      (await request<{ data: AutoRepeatInfo[] }>('GET', `${resource(doctype, name)}/auto-repeat`)).data,
    create: async (doctype: string, name: string, body: AutoRepeatRequest) =>
      (await request<{ data: AutoRepeatInfo }>('POST', `${resource(doctype, name)}/auto-repeat`, body)).data,
    update: async (id: string, body: AutoRepeatRequest) =>
      (await request<{ data: AutoRepeatInfo }>('PUT', `/api/auto-repeat/${encodeURIComponent(id)}`, body)).data,
    runNow: async (id: string) => (await request<{ data: AutoRepeatInfo }>('POST', `/api/auto-repeat/${encodeURIComponent(id)}/run`)).data,
    remove: async (id: string) => { await request<unknown>('DELETE', `/api/auto-repeat/${encodeURIComponent(id)}`) },
  },

  assignmentRules: {
    list: async () => (await request<{ data: AssignmentRuleItem[] }>('GET', '/api/assignment-rules/doctypes')).data,
    get: async (name: string) =>
      (await request<{ data: AssignmentRuleView }>('GET', `/api/assignment-rules/doctypes/${encodeURIComponent(name)}`)).data,
    save: async (name: string, rules: AssignmentRuleSet, modified: string | null | undefined) =>
      (await request<{ data: AssignmentRuleView }>('PUT', `/api/assignment-rules/doctypes/${encodeURIComponent(name)}`, { rules, modified })).data,
    remove: async (name: string) => { await request<unknown>('DELETE', `/api/assignment-rules/doctypes/${encodeURIComponent(name)}`) },
  },

  workflows: {
    list: async () => (await request<{ data: WorkflowItem[] }>('GET', '/api/workflows/doctypes')).data,
    get: async (name: string) =>
      (await request<{ data: WorkflowView }>('GET', `/api/workflows/doctypes/${encodeURIComponent(name)}`)).data,
    save: async (name: string, workflow: WorkflowDefinition, modified: string | null | undefined) =>
      (await request<{ data: WorkflowView }>('PUT', `/api/workflows/doctypes/${encodeURIComponent(name)}`, { workflow, modified })).data,
    remove: async (name: string) => { await request<unknown>('DELETE', `/api/workflows/doctypes/${encodeURIComponent(name)}`) },
    status: async (doctype: string, name: string) =>
      (await request<{ data: WorkflowStatus | null }>('GET', `${resource(doctype, name)}/workflow`)).data,
    apply: async (doctype: string, name: string, action: string, modified: string | null | undefined) =>
      (await request<{ data: WorkflowStatus }>('POST', `${resource(doctype, name)}/workflow`, { action, modified })).data,
    pending: async () => (await request<{ data: PendingAction[] }>('GET', '/api/workflow/pending')).data,
  },

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
