import { createContext, useContext } from 'react'

/** Which document the form is showing, for fields that need to know (an attachment is uploaded to the document). */
export interface DocScope {
  doctype: string
  /** The saved document's name; null while it is still new. */
  name: string | null
}

export const DocContext = createContext<DocScope | null>(null)

export const useDocScope = (): DocScope | null => useContext(DocContext)
