# ADR-025: Print formats and letter heads

**Status:** accepted (L9, part 2)

## Context
Documents must be printable: an invoice, a delivery note, a certificate. Layouts differ per company, so they are data that a System Manager designs in the Desk. Producing PDFs on the server needs a rendering engine (a browser or a library) and a lot of attack surface; browsers already print any page well and "Save as PDF" is built into every one.

## Decision
- **A printout is an HTML page** made on the server and opened in its own tab, where a Print button (or Ctrl+P, "Save as PDF") produces paper or a file. `@media print` rules hide the button and remove margins. The Desk's Print dialog shows the same page in a sandboxed preview frame.
- **Print Format** (Core DocType `Print Format`, written only by `PrintService`): a name, the DocType, the HTML, "default for this DocType", and an optional letter head. At most 20 per DocType. **Letter Head** (`Letter Head`): a header and a footer of HTML printed around every page; one may be the default; a format may name its own; "none" is a choice when printing.
- **The template language** (`PrintTemplate`) is deliberately tiny: `{{field}}` (value), `{{label:field}}` (the field's label), `{{#if field}}…{{else}}…{{/if}}`, `{{#each table}}…{{/each}}` with `{{@index}}`, and the specials `name`, `doctype`, `today`, `user`, `status`. No expressions, no code, no includes, nesting at most 6 deep, tables not nested, output at most 2 MB. Names are checked against the DocType when the format is saved, with messages in words ("'nope' is not a field of this DocType"). Currency prints as `1,234.50`, checks as Yes/No, dates ISO.
- **Without a format** a clean default layout is generated: the title, every field the person can see, each table.
- **Values are always HTML-escaped** (long text keeps its line breaks), a value that looks like `{{…}}` is printed as text and never expanded again, and password fields never print.
- **The document is read with the viewer's own permissions** (`DocumentService.GetAsync`), so fields above their permission level are simply absent from the printout, own-only roles and User Permissions apply, and printing needs read access. Print Format and Letter Head HTML is only readable by System Managers, who also design them (with a preview on a real document they can read).

## Security
The HTML a manager writes is checked when saved and refused if it contains `script`, `iframe`, `object`, `embed`, `form` controls, `svg`, `meta`, `base`, `link` and similar tags, event attributes (`onclick=`…), `javascript:` or `data:` links (except small embedded images), or style rules that import files or load anything but `https:` and embedded images. As a second, independent layer the printable page is served with `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; img-src data: https:; script-src 'nonce-…'; base-uri 'none'; form-action 'none'`, `X-Content-Type-Options: nosniff` and `Cache-Control: no-store`: the only script that can run is the page's own Print button (a fresh random nonce per request), so even a construct that slipped past the checks could not execute. The in-app preview uses `<iframe sandbox="">` (no scripts, no same-origin). Format names are restricted to simple characters. Tests cover a long list of hostile templates, hostile document values, field levels, permissions and the response headers.

## Consequences
- Not yet: server-side PDF files (for email attachments and automatic archiving), barcodes and QR codes, page numbers and page size options, a drag-and-drop layout designer, per-language formats, and print settings (default page size, margins).
- Emailing a document as PDF will build on this when a PDF renderer is added.
