# ADR-050: PDF files of printouts

**Status:** accepted (L9, part 5)

## Context
Printouts are HTML (ADR-020 and later), which people can "Save as PDF" from the browser, but a PDF made on the server is needed to download one in a click, attach it to an email or call it from another system. .NET has no PDF writer; libraries that draw text themselves either lack proper Arabic shaping or carry a commercial licence, and the printout is already a finished HTML page with the right fonts and right-to-left layout.

## Decision
- **A headless browser prints the printout.** `PdfService` renders the same page as the preview (same formats, letter heads, permission check) and hands it to an `IPdfRenderer`. The one real implementation starts the machine's **Chrome, Chromium or Edge** in headless mode (`Qahwa:Pdf:BrowserPath`, or found in the usual places on Windows, Linux and macOS). Nothing is added to the application, there is no licence to accept, and Arabic and other scripts come out as they do on screen. Where no browser is available the endpoint answers 501 with a plain explanation, the Print dialog hides "Download PDF", and "Save as PDF" in the browser still works.
- **The page is made inert first.** Scripts, the Print button, `link`, `iframe`, `object`, `embed`, `base` and meta refresh tags are removed, and a content-security policy (`default-src 'none'; img-src data:; font-src data:`) is written as the first tag of the page. Logos in a letter head must therefore be **embedded** (`data:` pictures): a format or letter head can never make the server fetch an address.
- **The browser is contained.** It is started without a shell (arguments are a list), in a folder of its own with its own profile, deleted afterwards; with no network (a dead proxy, so even an address written as numbers goes nowhere, and every name fails to resolve); with a time limit after which it is killed; with a limit on the size of the result; and the output must start with `%PDF`. At most two run at once (`Concurrency`); others wait ten seconds, then get 429. The browser's own sandbox stays on unless `NoSandbox` is set (needed when running as root in some containers).
- **Permissions and limits.** Whoever may read the document may have its PDF (checked before anything is rendered); a person can make ten a minute; the file name is reduced to letters, digits, dash and underscore; the answer is a download, `no-store`.
- **Endpoints:** `GET /api/pdf/status`, `GET /api/pdf/{doctype}/{name}?format=&letterhead=`.

## Consequences
- Start-up cost of a browser per PDF (about two seconds here); fine for downloads, not for thousands per minute (a pool of browsers would be the next step).
- Not yet: PDF of reports and lists, page headers and footers with page numbers, attaching a PDF to an email from the Email dialog, a PDF "page size" option per format.
