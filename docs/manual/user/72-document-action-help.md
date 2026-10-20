# Writing Document Actions with help

See [Document actions](25-document-actions.md). On the **Document Action** form, the helper under each box lists the fields of the document you are automating and of the document to create:

- **Fields to watch / set:** click a field to fill the box.
- **Existing document:** the values you can use as `{{field}}`, plus `{{name}}`, `{{doctype}}` and `{{user}}`.
- **New document:** each field of the DocType to create with an example line; click to add it to the mapping, then replace sample values such as *your value* before saving.
- A red note appears when **Submit** or **Cancel** is chosen for a DocType that cannot be submitted, or when the DocType to create has a required table (actions cannot fill tables).

Saving is refused if a mapping line is not `target_field = value`, or if a target field is mapped twice.
