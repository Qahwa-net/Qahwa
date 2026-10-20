export type FieldType =
  | 'Data' | 'Text' | 'LongText' | 'Int' | 'Float' | 'Decimal' | 'Currency' | 'Percent'
  | 'Check' | 'Select' | 'Date' | 'Datetime' | 'Time' | 'Link' | 'DynamicLink' | 'Table'
  | 'Attach' | 'AttachImage' | 'Json' | 'Password' | 'Email' | 'Phone' | 'Url' | 'Code'
  | 'Markdown' | 'SectionBreak' | 'ColumnBreak' | 'TabBreak' | 'Heading' | 'Html'
  | 'Icon' | 'Rating' | 'Duration' | 'Color' | 'Barcode' | 'Geolocation' | 'Signature' | 'SmallText' | 'Autocomplete' | 'TextEditor'

export type IndicatorColor = 'gray' | 'blue' | 'green' | 'orange' | 'red' | 'purple'

export interface Indicator {
  value: string
  color: IndicatorColor
}

export interface DocField {
  name: string
  type: FieldType
  label?: string | null
  /** Link target DocType, child DocType for Table, or newline-separated Select options. */
  options?: string | null
  required: boolean
  unique: boolean
  inListView: boolean
  default?: string | null
  lowercase: boolean
  description?: string | null
  hidden?: boolean
  readOnly?: boolean
  /** Expressions: see lib/expr. The field is shown / required / read-only while they are true. */
  dependsOn?: string | null
  mandatoryDependsOn?: string | null
  readOnlyDependsOn?: string | null
  /** Calculated from other fields. */
  formula?: string | null
  /** "link_field.target_field": copy a value from the linked document. */
  fetchFrom?: string | null
  fetchIfEmpty?: boolean
  /** 0 is the document's own access; 1–9 need rules at that level (see the Role Permission Manager). */
  permLevel?: number
  /** Submittable DocTypes: may still be changed after the document is submitted. */
  allowOnSubmit?: boolean
  setOnce?: boolean
  nonNegative?: boolean
  maxLength?: number | null
  /** Table fields only: shown as a list of chips to pick from (the child table has one Link field). */
  multiSelect?: boolean
}

export interface ValidationRule {
  expression: string
  message: string
  field?: string | null
}

export interface DocPerm {
  role: string
  read: boolean
  write: boolean
  create: boolean
  delete: boolean
  ifOwner: boolean
  permLevel?: number
  submit?: boolean
  cancel?: boolean
  export?: boolean
  report?: boolean
  import?: boolean
  share?: boolean
  print?: boolean
  email?: boolean
  amend?: boolean
}

export interface DocTypeMeta {
  name: string
  module?: string | null
  titleField?: string | null
  autoname?: string | null
  isChildTable: boolean
  isSubmittable: boolean
  /** One document, named after the DocType (settings). It has no list. */
  isSingle?: boolean
  /** Documents form a tree through parent_node and is_group. */
  isTree?: boolean
  /** Documents may be renamed or merged. */
  allowRename?: boolean
  /** The field shown as a coloured status in lists, and the colour of each of its values. */
  statusField?: string | null
  indicators?: Indicator[]
  trackChanges: boolean
  trackViews?: boolean
  fields: DocField[]
  permissions?: DocPerm[]
  validations?: ValidationRule[]
}

/** The definition sent to the designer API. Optional flags may be omitted. */
export interface DocTypeDefinition {
  name: string
  module?: string
  titleField?: string
  autoname?: string
  isChildTable?: boolean
  isSubmittable?: boolean
  isSingle?: boolean
  isTree?: boolean
  allowRename?: boolean
  trackChanges?: boolean
  trackViews?: boolean
  statusField?: string
  indicators?: Indicator[]
  fields: (Partial<DocField> & Pick<DocField, 'name' | 'type'>)[]
  permissions: DocPerm[]
  validations?: ValidationRule[]
}

export type DocTypeSource = 'Core' | 'App' | 'Custom'

export interface DesignerItem {
  name: string
  module?: string | null
  source: DocTypeSource
  editable: boolean
  fieldCount: number
  isChildTable: boolean
}

export interface DesignerDetail {
  definition: DocTypeMeta
  source: DocTypeSource
  editable: boolean
  modified?: string | null
  documentCount?: number | null
}

export type OptionsKind = 'None' | 'Choices' | 'DocType' | 'Html'

export interface FieldTypeInfo {
  type: FieldType
  label: string
  group: string
  hint: string
  options: OptionsKind
  supported: boolean
  unsupportedReason?: string | null
}

export interface SchemaChange {
  description: string
  sql: string
}

export interface DesignerPreview {
  errors: FieldError[]
  schema: { tableExists: boolean; changes: SchemaChange[]; warnings: string[] } | null
  notes: string[]
}

export interface MetaSummary {
  name: string
  module?: string | null
  titleField?: string | null
}

export interface Permissions {
  read: boolean
  write: boolean
  create: boolean
  delete: boolean
  submit?: boolean
  cancel?: boolean
  export?: boolean
  report?: boolean
  import?: boolean
  share?: boolean
  print?: boolean
  email?: boolean
  amend?: boolean
}

export interface MetaResponse {
  data: DocTypeMeta
  permissions: Permissions
  /** The child DocTypes used by this DocType's Table fields, keyed by name. */
  children: Record<string, DocTypeMeta>
  /** The permission levels this user may write at; fields at other levels are read-only. */
  writableLevels?: number[]
}

export interface PermissionItem {
  name: string
  module?: string | null
  source: DocTypeSource
  customized: boolean
  ruleCount: number
}

export interface PermissionView {
  name: string
  source: DocTypeSource
  customized: boolean
  modified?: string | null
  rules: DocPerm[]
  defaultRules: DocPerm[]
  /** Fields above level 0, so the matrix can say what each level protects. */
  fields: { name: string; label?: string | null; level: number }[]
  canEdit: boolean
  isSubmittable?: boolean
}

/** One choice in a link field: the stored value, what to show, and a smaller hint (usually the ID). */
export interface LinkOption {
  value: string
  label: string
  description?: string | null
}

export interface ShareInfo {
  user: string
  fullName?: string | null
  read: boolean
  write: boolean
}

/** Changes to a standard field's properties. Absent means unchanged; an empty text clears the value. */
export interface FieldOverride {
  label?: string
  description?: string
  hidden?: boolean
  readOnly?: boolean
  required?: boolean
  default?: string
  inListView?: boolean
  permLevel?: number
  options?: string
  dependsOn?: string
  mandatoryDependsOn?: string
  readOnlyDependsOn?: string
}

/** What the Customize Form changed on a DocType: new fields, changed properties, order and title field. */
export interface Customization {
  customFields: DocField[]
  overrides: Record<string, FieldOverride>
  order: string[]
  /** Empty restores the definition's title field; absent leaves it. */
  titleField?: string | null
}

export interface CustomizationItem {
  name: string
  module?: string | null
  source: DocTypeSource
  customized: boolean
  customFieldCount: number
}

export interface CustomizationView {
  name: string
  source: DocTypeSource
  customized: boolean
  modified?: string | null
  /** The DocType as defined, before any customisation. */
  definition: DocTypeMeta
  /** The DocType as it works now. */
  effective: DocTypeMeta
  customization: Customization
  canEdit: boolean
}

/** One entry of a document's timeline: an update from the log, or a comment or system note. */
export interface ActivityItem {
  kind: 'version' | 'comment'
  id: string
  /** For versions Insert, Update, Submit, Cancel, Rename, Merge or Delete; for comments Comment or Info. */
  event: string
  user: string
  userName?: string | null
  at: string
  /** For versions: `changed` ([field, before, after] entries) and `tables`, limited to what the viewer may read. */
  data?: { changed?: unknown[]; tables?: Record<string, unknown>; from?: string; to?: string } | null
  content?: string | null
  edited: boolean
  canEdit: boolean
}

export interface NotificationItem {
  id: string
  type: 'Mention' | 'Assignment' | 'Comment' | 'Update' | 'Share'
  docType: string
  docName: string
  from: string
  fromName?: string | null
  subject: string
  read: boolean
  at: string
}

export interface EmailDigest {
  enabled: boolean
  frequency: 'Daily' | 'Weekly'
  sendHour: number
  nextSend: string | null
  lastSent: string | null
}

export interface Viewer {
  user: string
  fullName: string | null
  views: number
  lastViewed: string
}

export interface ScriptReportInfo {
  id: string
  title: string
  app: string
  filters: string[]
}

export interface ScriptReportResult {
  id: string
  title: string
  columns: { key: string; label: string; type: string }[]
  rows: Record<string, unknown>[]
  truncated: boolean
}

export interface AppInfo {
  name: string
  title: string
  version: string
  installed: boolean
}

export interface OAuthAuthorizeParams {
  clientId: string
  redirectUri: string
  scope: string
  state: string
  codeChallenge: string
  codeChallengeMethod: string
}

export interface OAuthConsent {
  clientName: string
  redirectHost: string
  scopes: { name: string; description: string }[]
}

export interface OAuthClientInfo {
  id: string
  clientId: string
  name: string
  redirectUris: string[]
  scopes: string[]
  public: boolean
  enabled: boolean
  created: string
}

export interface OAuthConnected {
  clientId: string
  name: string
  scope: string
  since: string
}

export interface LikeState {
  liked: boolean
  count: number
}

export interface TagCount {
  tag: string
  count: number
}

export interface FollowState {
  following: boolean
  followers: number
}

export interface AssignmentInfo {
  id: string
  user: string
  userName?: string | null
  assignedBy: string
  assignedByName?: string | null
  status: 'Open' | 'Closed' | 'Cancelled'
  priority: 'Low' | 'Medium' | 'High'
  due?: string | null
  description?: string | null
  at: string
  /** Only in "my assignments": the document it is about. */
  docType?: string | null
  docName?: string | null
}

/** How one person likes a DocType's list: columns, rows per page and sort order. */
export interface ListViewSettings {
  columns: string[]
  pageSize: number
  sortField?: string | null
  sortDescending: boolean
}

/** A named set of list filters, private or shared with everyone who can use the DocType. */
export interface SavedFilter {
  id: string
  name: string
  /** [field, operator, value] entries, in the same shape as the list's filters. */
  filters: [string, string, string | number | boolean][] | null
  isPublic: boolean
  mine: boolean
  owner: string
  ownerName?: string | null
}

export interface ListViewState {
  settings: ListViewSettings | null
  filters: SavedFilter[]
}

export interface BulkResult {
  name: string
  ok: boolean
  error?: string | null
}

/** How many documents have one value of a field (null is "empty"). */
export interface GroupCountItem {
  value: string | null
  count: number
}

export interface TreeNode {
  name: string
  is_group: boolean
  [field: string]: unknown
}

export interface ReportRow {
  group: string | null
  value: number
}

export interface SeriesInfo {
  prefix: string
  digits: number
  current: number
  docTypes: string[]
}

export type Doc = Record<string, unknown> & { name: string }

export interface ListResponse {
  data: Doc[]
  total: number
}

export interface FieldError {
  field: string
  message: string
}

export interface SessionUser {
  user: string
  full_name: string
  roles: string[]
}

/** One state of a workflow. docStatus: 0 draft, 1 submitted, 2 cancelled. */
export interface WorkflowState {
  name: string
  color: IndicatorColor
  docStatus: number
  editRoles: string[]
}

export interface WorkflowTransition {
  from: string
  action: string
  to: string
  roles: string[]
  condition?: string | null
  allowSelfApproval: boolean
}

export interface WorkflowDefinition {
  docType: string
  stateField: string
  active: boolean
  states: WorkflowState[]
  transitions: WorkflowTransition[]
}

export interface WorkflowItem {
  docType: string
  module?: string | null
  source: DocTypeSource
  hasWorkflow: boolean
  active: boolean
  states: number
}

export interface WorkflowView {
  docType: string
  source: DocTypeSource
  exists: boolean
  modified?: string | null
  workflow: WorkflowDefinition
  stateFields: string[]
  canEdit: boolean
}

export interface WorkflowAction {
  action: string
  to: string
}

/** Where one document is in its workflow and what the current user can do with it. */
export interface WorkflowStatus {
  stateField: string
  state: string
  color: IndicatorColor
  actions: WorkflowAction[]
  canEdit: boolean
}

export interface PendingAction {
  docType: string
  name: string
  title?: string | null
  state: string
  color: IndicatorColor
  modified: string
  actions: WorkflowAction[]
}

export type AssignmentStrategy = 'RoundRobin' | 'LoadBalancing' | 'Field'

export interface AssignmentRule {
  name: string
  active: boolean
  strategy: AssignmentStrategy
  users: string[]
  userField?: string | null
  condition?: string | null
  closeCondition?: string | null
  description?: string | null
  dueInDays?: number | null
  priority: 'Low' | 'Medium' | 'High'
}

export interface AssignmentRuleSet {
  docType: string
  rules: AssignmentRule[]
}

export interface AssignmentRuleItem {
  docType: string
  module?: string | null
  source: DocTypeSource
  hasRules: boolean
  rules: number
  activeRules: number
}

export interface AssignmentRuleView {
  docType: string
  source: DocTypeSource
  exists: boolean
  modified?: string | null
  set: AssignmentRuleSet
  userFields: string[]
  canEdit: boolean
}

export type RepeatFrequency = 'Daily' | 'Weekly' | 'Monthly' | 'Quarterly' | 'Yearly'

/** A document that copies itself on a schedule. */
export interface AutoRepeatInfo {
  id: string
  docType: string
  name: string
  frequency: RepeatFrequency
  startDate: string
  endDate?: string | null
  nextDate?: string | null
  status: 'Active' | 'Paused' | 'Finished'
  dateFields: string[]
  created: number
  lastRun?: string | null
  lastCreated?: string | null
  lastError?: string | null
  owner: string
  ownerName?: string | null
}

export interface AutoRepeatRequest {
  frequency?: RepeatFrequency
  startDate?: string
  endDate?: string
  dateFields?: string[]
  status?: 'Active' | 'Paused'
}

export type NotificationEvent = 'New' | 'Update' | 'ValueChange' | 'Submit' | 'Cancel' | 'DaysBefore' | 'DaysAfter'

export interface NotificationRule {
  name: string
  active: boolean
  event: NotificationEvent
  field?: string | null
  dateField?: string | null
  days?: number | null
  condition?: string | null
  users: string[]
  roles: string[]
  userFields: string[]
  notifyOwner: boolean
  notifyFollowers: boolean
  email: boolean
  message: string
}

export interface NotificationRuleSet {
  docType: string
  rules: NotificationRule[]
}

export interface NotificationRuleItem {
  docType: string
  module?: string | null
  source: DocTypeSource
  hasRules: boolean
  rules: number
  activeRules: number
}

export interface NotificationRuleView {
  docType: string
  source: DocTypeSource
  exists: boolean
  modified?: string | null
  set: NotificationRuleSet
  userFields: string[]
  fields: string[]
  dateFields: string[]
  canEdit: boolean
}

export interface EmailTemplateInfo {
  name: string
  docType?: string | null
}

export interface EmailPreview {
  subject: string
  message: string
}

export type TileType = 'Number' | 'Chart' | 'List'
export type TileFunction = 'count' | 'sum' | 'avg' | 'min' | 'max'

export interface TileFilter {
  field: string
  op: string
  value?: string | number | boolean | null
}

/** One tile of a dashboard. */
export interface DashboardItem {
  id: string
  type: TileType
  title: string
  docType: string
  function: TileFunction
  valueField?: string | null
  groupBy?: string | null
  period?: 'day' | 'month' | 'year' | null
  chartType: 'bar' | 'line' | 'pie'
  columns: string[]
  orderBy?: string | null
  descending: boolean
  limit: number
  filters: TileFilter[]
  color: IndicatorColor
}

export interface DashboardInfo {
  id: string
  title: string
  isPublic: boolean
  mine: boolean
  owner: string
  ownerName?: string | null
  items: number
  modified: string
}

export interface DashboardDetail {
  info: DashboardInfo
  items: DashboardItem[]
  canEdit: boolean
}

export interface DashboardResult {
  id: string
  type: TileType
  number?: number | null
  points?: { label: string | null; value: number }[] | null
  columns?: string[] | null
  rows?: Record<string, unknown>[] | null
  truncated: boolean
  error?: string | null
}

export interface PrintFormatInfo {
  name: string
  docType: string
  isDefault: boolean
  letterHead?: string | null
}

export interface PrintFormatDetail extends PrintFormatInfo {
  html: string
}

export interface PrintChoices {
  formats: PrintFormatInfo[]
  letterHeads: string[]
}

export interface LetterHeadDetail {
  name: string
  header: string
  footer: string
  isDefault: boolean
}

export interface WorkspaceShortcut {
  label: string
  kind: 'DocType' | 'Dashboard' | 'Page'
  target: string
}

export interface WorkspaceBlock {
  type: 'Heading' | 'Shortcuts' | 'Dashboard'
  text?: string | null
  items: WorkspaceShortcut[]
  dashboardId?: string | null
}

export interface WorkspaceInfo {
  id: string
  title: string
  sequence: number
  roles: string[]
  blocks: number
}

export interface WorkspaceDetail {
  info: WorkspaceInfo
  blocks: WorkspaceBlock[]
}

export interface WorkspaceBlockView {
  type: 'Heading' | 'Shortcuts' | 'Dashboard'
  text?: string | null
  shortcuts?: { label: string; kind: string; path: string }[] | null
  dashboardId?: string | null
  dashboardTitle?: string | null
}

export interface WorkspaceView {
  id: string
  title: string
  blocks: WorkspaceBlockView[]
}

export interface ImportColumn {
  header: string
  field?: string | null
  label?: string | null
}

export interface ImportRowError {
  row: number
  field?: string | null
  message: string
}

export interface ImportPreview {
  columns: ImportColumn[]
  rows: number
  sample: string[][]
  errors: ImportRowError[]
  moreErrors: boolean
}

export interface ImportResult {
  created: number
  updated: number
  failed: number
  errors: ImportRowError[]
  stopped: boolean
  moreErrors: boolean
}

export interface ApiKeyInfo {
  id: string
  keyId: string
  user: string
  description?: string | null
  readOnly: boolean
  expiresOn?: string | null
  created: string
  lastUsed?: string | null
  expired: boolean
}

export interface ApiKeyCreated {
  info: ApiKeyInfo
  token: string
}

export type WebhookEvent = 'New' | 'Update' | 'Submit' | 'Cancel' | 'Delete'

export interface Webhook {
  name: string
  active: boolean
  event: WebhookEvent
  condition?: string | null
  url: string
  /** Write-only: blank keeps the stored secret. */
  secret?: string | null
  secretSet: boolean
  fields: string[]
}

export interface WebhookSet {
  docType: string
  hooks: Webhook[]
}

export interface WebhookItem {
  docType: string
  module?: string | null
  source: DocTypeSource
  hasHooks: boolean
  hooks: number
  activeHooks: number
}

export interface WebhookView {
  docType: string
  source: DocTypeSource
  exists: boolean
  modified?: string | null
  set: WebhookSet
  fields: string[]
  canEdit: boolean
}

export interface AttachedFile {
  id: string
  fileName: string
  contentType: string
  size: number
  image: boolean
  uploadedBy: string
  uploadedByName?: string | null
  created: string
}

export type BundleKind = 'DocType' | 'Customization' | 'Workflow' | 'AssignmentRules' | 'NotificationRules'

export interface BundleItemResult {
  kind: BundleKind
  docType: string
  action: string
  ok: boolean
  message: string
}

export interface BundleReport {
  dryRun: boolean
  applied: number
  failed: number
  items: BundleItemResult[]
}

export interface TranslationCatalog {
  language: string
  rtl: boolean
  messages: Record<string, string>
}

export interface PublicFormField {
  name: string
  label: string
  type: string
  required: boolean
  options: string[]
  description: string | null
}

export interface PublicForm {
  route: string
  title: string
  intro: string | null
  fields: PublicFormField[]
}

export interface TwoFactorChallenge {
  two_factor: true
  token: string
}

export interface ExpiredPasswordChallenge {
  password_expired: true
  token: string
}

export interface TwoFactorStatus {
  enabled: boolean
  recoveryCodesLeft: number
}

export interface TwoFactorSetup {
  secret: string
  uri: string
}

export interface SignIn {
  id: string
  created: string
  last_seen: string
  address: string | null
  agent: string | null
  current: boolean
}

export interface RestoreTableReport {
  docType: string
  inserted: number
  existing: number
  failed: number
  firstProblem: string | null
}

export interface RestoreReport {
  dryRun: boolean
  definitions: BundleItemResult[]
  tables: RestoreTableReport[]
  seriesRaised: number
}

export interface ReportInfo {
  id: string
  title: string
  docType: string
  isPublic: boolean
  mine: boolean
  owner: string
  ownerName: string | null
  modified: string
}

export interface ReportDetail {
  info: ReportInfo
  definition: Record<string, unknown>
  canEdit: boolean
}

export interface ReportColumn {
  key: string
  label: string
  type: string
}

export interface ReportRun {
  columns: ReportColumn[]
  rows: Record<string, unknown>[]
  truncated: boolean
  grouped: boolean
}
