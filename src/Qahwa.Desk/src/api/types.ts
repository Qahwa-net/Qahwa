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
}

export interface DocTypeMeta {
  name: string
  module?: string | null
  titleField?: string | null
  autoname?: string | null
  isChildTable: boolean
  isSubmittable: boolean
  trackChanges: boolean
  fields: DocField[]
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
}

export interface MetaResponse {
  data: DocTypeMeta
  permissions: Permissions
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
