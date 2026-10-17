import { createContext } from 'react'
/** Avoid stacking quick-create dialogs while the current linked document is being created. */
export const InQuickCreate = createContext(false)
