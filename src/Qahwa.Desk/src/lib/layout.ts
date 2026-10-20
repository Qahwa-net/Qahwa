import type { DocField } from '../api/types.ts'

export interface FormSection {
  /** Empty for the fields before the first section break. */
  label: string
  fields: DocField[]
  /** The same fields in the columns a Column Break made (empty columns are dropped); one entry when the section has no column break. */
  columns: DocField[][]
}

export interface FormTab {
  label: string
  sections: FormSection[]
}

/**
 * Arranges a DocType's fields for the form: a tab break starts a tab, a section break starts a section (a titled card),
 * a column break starts the next column of the section (up to four). Fields before any break form an untitled section of the first tab. Fields the form
 * does not show (`shown` says false) are left out, and so are sections and tabs that end up empty.
 */
const MAX_COLUMNS = 4

export function buildLayout(fields: DocField[], shown: (field: DocField) => boolean = () => true, firstTab = 'Details'): FormTab[] {
  const tabs: FormTab[] = []
  let tab: FormTab = { label: firstTab, sections: [] }
  let section: FormSection = { label: '', fields: [], columns: [[]] }

  const closeSection = () => {
    section.columns = section.columns.filter((c) => c.length > 0)
    if (section.fields.length > 0) tab.sections.push(section)
  }
  const closeTab = () => {
    closeSection()
    if (tab.sections.length > 0) tabs.push(tab)
  }

  for (const field of fields) {
    if (field.type === 'TabBreak') {
      if (!shown(field)) continue
      closeTab()
      tab = { label: field.label || `Tab ${tabs.length + 2}`, sections: [] }
      section = { label: '', fields: [], columns: [[]] }
    } else if (field.type === 'SectionBreak') {
      if (!shown(field)) continue
      closeSection()
      section = { label: field.label ?? '', fields: [], columns: [[]] }
    } else if (field.type === 'ColumnBreak') {
      if (shown(field) && section.columns.length < MAX_COLUMNS && section.columns.at(-1)!.length > 0) section.columns.push([])
    } else if (shown(field)) {
      section.fields.push(field)
      section.columns.at(-1)!.push(field)
    }
  }
  closeTab()
  return tabs
}
