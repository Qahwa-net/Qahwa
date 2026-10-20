# ADR-110: Keyboard shortcuts

Status: accepted

## Decision
Ctrl/⌘+K (search), Ctrl/⌘+S (save), **Alt+N** (new document of the type on screen) and **?** (list the shortcuts). A plain-key shortcut such as `?` is ignored while the person is typing in a field; only Ctrl/⌘+K works there. Nothing here changes data except through the same forms and permission checks as the mouse. The logic is a small pure module (`lib/shortcuts.ts`) so it can be tested without a browser.

Saving to the server without a click (true auto-save) is left out on purpose: a half-finished document could be validated, notified and logged on every pause. Unsaved typing is kept in the browser instead (ADR-109), and the form shows a "Not saved" badge and warns before leaving.
