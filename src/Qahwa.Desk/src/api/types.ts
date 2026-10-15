export type FieldType =
  | 'Data' | 'Text' | 'LongText' | 'Int' | 'Float' | 'Decimal' | 'Currency' | 'Percent'
  | 'Check' | 'Select' | 'Date' | 'Datetime' | 'Time' | 'Link' | 'DynamicLink' | 'Table'
  | 'Attach' | 'AttachImage' | 'Json' | 'Password' | 'Email' | 'Phone' | 'Url' | 'Code'
  | 'Markdown' | 'SectionBreak' | 'ColumnBreak' | 'TabBreak'

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

export type OptionsKind = 'None' | 'Choices' | 'DocType'

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
