# Document actions (automation without code)

A **Document Action** does something automatically when a document is created, changed, submitted or cancelled. Open **Document Action** in the sidebar (System Manager) and choose **New**.

| Field | Meaning |
|---|---|
| Title | A name for you |
| Active | Untick to switch it off |
| When this DocType changes | The DocType the action belongs to, for example `Ticket` |
| Event | New, Update, ValueChange (choose the field to watch), Submit or Cancel |
| Only when | Optional condition, for example `priority == 'High'` |
| Do this | **Set Field** or **Create Document** |

## Set Field
Give the **field** of the same document and the **value**. The value is text; `{{field}}` is replaced by that field's value, and `{{name}}`, `{{doctype}}` and `{{user}}` (who made the change) are available. Example: field `status`, value `Opened by {{user}}`.

## Create Document
Give the **DocType to create** and one line per field in **mapping**:

```
customer = {{customer}}
subject = Follow up {{name}}
```

## Good to know
- The action runs **as the person who made the change**, with their rights. If they may not do it, nothing happens and **Last problem** says why.
- A change made by an action does **not** start other actions, so actions cannot loop.
- A broken action never stops the document from being saved. Check **Last problem** and **Times run**.
- Changes take effect within seconds (immediately on the server where you saved).
- You cannot set passwords, attachments, tables or calculated fields, or create framework documents.
