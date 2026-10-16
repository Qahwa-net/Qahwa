# Attachment and image fields

A DocType can have **Attachment** fields (any file, for example a receipt) and **Image** fields (a picture, for example a photo) as well as the general **Attachments** list in the side panel.

- **Save the document first.** A new document has nowhere to keep a file yet; after the first save, choose the file in the field. It is uploaded at once and appears in the side panel too.
- **Image** fields show the picture on the form. Only real pictures (PNG, JPEG, GIF, WebP) are accepted, whatever the file is called.
- **Remove from this field** empties the field but keeps the file in the side panel. Removing the file from the side panel empties any field that used it.
- A field can only use a file that belongs to the same document.
- Files follow the usual rules: up to 25 MB each, and the same permissions as the document.

When designing a DocType, add the field in the **DocType Designer**. They cannot be used inside a table of rows.
