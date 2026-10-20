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

## Help while you write
On the form, the boxes for **field to watch**, **field to set**, **value** and **mapping** show the fields of the DocTypes involved. Click a field to fill the box, or click an example line to add it to the mapping (replace sample values before saving). The form warns you when the event cannot work, for example **Submit** or **Cancel** on a DocType that is not submittable (use ValueChange on a workflow field, or New / Update), or when the DocType to create has a required table that an action cannot fill.

Saving is refused when a mapping line is not written `target_field = value` or when the same target field appears twice.

## Good to know
- The action runs **as the person who made the change**, with their rights. If they may not do it, nothing happens and **Last problem** says why.
- A change made by an action does **not** start other actions, so actions cannot loop.
- A broken action never stops the document from being saved. Check **Last problem** and **Times run**.
- Changes take effect within seconds (immediately on the server where you saved).
- You cannot set passwords, attachments, tables or calculated fields, or create framework documents.
