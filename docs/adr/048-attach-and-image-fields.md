# ADR-048: Attachment and Image fields

**Status:** accepted (L10, part 7)

## Context
Files already attach to documents (ADR-030), shown in the side panel. A form often needs one particular file in one particular place: the receipt of a claim, the photo of a product.

## Decision
- **A field that names an attached file.** `Attach` and `Attach Image` fields store the **id of a File** that is attached to the same document. The file itself is uploaded through the existing, already-hardened path (permission checks, size and count limits, content-sniffed type, raw bytes with a header), so these fields add no new way to upload.
- **Validated on every save** (only when the field changes): the file must exist, must be attached to **this very document** (a field can never point at someone else's file, even one the person can see), and for an Image field must be a real PNG, JPEG, GIF or WebP by its first bytes. A **new** document cannot name a file (none can be attached to a document that does not exist yet): the Desk says "Save the document first, then attach the file".
- **Consistency.** When a file is removed (side panel or field), any field naming it is emptied, so nothing points at a file that is gone; removing the file from the field alone keeps it in the side panel. Deleting the document deletes its files as before.
- **Not in child tables** (the definition is refused): attach the file to the document itself.
- **Downloads and previews** use the existing file endpoint, checked against the document's read permission each time; pictures show inline on the form, everything else is a download with the safe headers of ADR-030.
- Printouts and lists show "(attached)" / "(file)", never the id; OpenAPI describes the field as a file id; the Designer offers both types; web forms do not accept them.

## Consequences
- Not yet: attaching in the same step as creating a document, several files in one field, drag and drop onto the field, thumbnails in lists, image resizing.
