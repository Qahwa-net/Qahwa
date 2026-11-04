# Customize Form: change standard DocTypes

**Tools → Customize Form** (System Managers). Use it when you need one more field on something that ships with Qahwa or an app (for example a *Phone* on User), or when a standard field needs a different label, to be required, or hidden. The original DocType is never edited, so you can always go back.

## What you can do

- **Add a custom field**: choose a type, type a label, click **Add**. Its name starts with `custom_`. It gets its own column in the database and appears in forms and lists at once.
- **Change a standard field**: click its name to open it. You can change the label, help text, default value, required, read only, show in list, hidden, permission level, the choices of a choice field, and the three conditions (show only when / required when / read-only when).
- **Reorder** fields with the ↑ and ↓ buttons.
- **Title field**: choose which field names a document in lists and links.
- **Save** applies your changes; **Reset** removes every customisation of that DocType.

## Good to know

- Removing a custom field (or Reset) hides it but **keeps its data**; add it again and the data returns.
- A custom field marked **Required** makes older documents fail to save until it is filled.
- You cannot change a field's type, what a link points at, or whether it is unique here.
- Only an **Administrator** can customise the framework's own DocTypes (User, Role ...). System Managers can customise app DocTypes.
- DocTypes you made yourself are changed in the DocType Designer, and child tables cannot be customised yet.
- If two people customise the same DocType, the second save is refused; reload and try again.

## A simpler form for some roles (DocType Layout)
Open **Tools → DocType Layout** (System Managers) and add a layout: choose the **DocType**, the **roles** it is for (one per line; leave empty for everyone) and the **fields to hide** (one field name per line, as written in the Designer). People with one of those roles then see the form without those fields. If several layouts fit someone, the one with the smallest **Position** is used; untick **Enabled** to switch one off.

A layout only changes what is shown. It does not take anything away: permissions and validation stay as they are, so a **required** field cannot be hidden (the layout will not save). Hidden fields are still saved if they are sent, for example by an import or the API.
