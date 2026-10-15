export type FieldType =
  | 'Data' | 'Text' | 'LongText' | 'Int' | 'Float' | 'Decimal' | 'Currency' | 'Percent'
  | 'Check' | 'Select' | 'Date' | 'Datetime' | 'Time' | 'Link' | 'DynamicLink' | 'Table'
  | 'Attach' | 'AttachImage' | 'Json' | 'Password' | 'Email' | 'Phone' | 'Url' | 'Code'
  | 'Markdown' | 'SectionBreak' | 'ColumnBreak' | 'TabBreak'

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
