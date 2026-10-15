# ADR-019: Document form UX: dialogs, icon actions and a side rail

**Status:** accepted

## Context
Assigning and sharing opened inline panels that pushed the form down, every action was a text button, and the form had no place for "who is involved". The form is where people spend most of their day, so it should be fast, calm and keyboard-friendly.

## Decision
- **Dialogs, not panels.** Assign, Share, Rename/Merge and every confirmation (submit, cancel, delete, merge) open in a `Modal` (`controls/Modal.tsx`): `role="dialog"`, `aria-modal`, labelled by its title, focus moves in and returns to the button that opened it, Tab stays inside, Escape or a click outside closes it (not while a save is running), the page behind does not scroll, and on phones it becomes a bottom sheet. `ConfirmDialog` replaces the browser's `window.confirm`.
- **Icon buttons with tooltips.** Follow (bell, filled when following, follower count), Assign, Share, Rename, Delete, Submit and Cancel are `IconButton`s (`controls/Icon.tsx`): an inline SVG line icon, an `aria-label` that is also the tooltip, tone (default, primary, danger), and disabled states that explain themselves ("Save your changes before submitting"). Only **Save** keeps a label (icon only on phones). Icons are decorative (`aria-hidden`), no icon font or image is loaded.
- **Sticky action bar.** The title, DocType name, status badges (Draft/Submitted/Cancelled, "Not saved") and the actions stay visible while scrolling. **Ctrl+S** (Cmd+S) saves.
- **Side rail** (`DocRail`) on saved documents: who it is assigned to (with priority and due date), who it is shared with (avatars), and details (ID, created, modified). The `+` buttons open the dialogs. On narrow screens the rail moves below the form.
- **Sections and tabs.** A section break becomes a titled card that can be collapsed; a tab break starts a tab (`lib/layout.ts` `buildLayout`, with arrow-key navigation and `role="tab"`). Fields hidden by logic are left out, and empty sections and tabs disappear. A tab with a validation error shows a red dot and a failed save jumps to the first such tab; a section with an error cannot stay collapsed.
- **Toasts** (`controls/Toast.tsx`, `useToast`) say "Created", "Saved", "Submitted", "Cancelled" and "Deleted"; they fade by themselves and are announced politely to screen readers. A grey **skeleton** shows while a form loads.
- **Activity** shows avatars, comment bubbles and quiet system notes; deleting a comment asks in a dialog.
- **People** are shown with an `Avatar` (initials on a colour derived from the name) rather than bare emails.

## Security
No change to the API or permissions: the same endpoints and server checks apply, and the buttons are shown only to people who hold the right (the server still decides). Text is rendered as text; colours come from fixed classes or a hue computed from a hash, never from stored markup.

## Consequences
- The old `AssignPanel`, `SharePanel` and `RenamePanel` inline cards are gone (their files now export `AssignDialog`, `ShareDialog`, `RenameDialog`).
- Not yet: the same treatment for the list view toolbar and the designer pages, a command palette, and a warning when leaving the app's page with unsaved edits (the browser warning on closing the tab exists).
