# ADR-030: Attachments (files on documents)

**Status:** accepted (L10, part 4)

## Context
Documents need files: a signed contract, a photo of a damaged parcel, a receipt. Serving user-supplied files is one of the most dangerous things a web application does (stored cross-site scripting through uploaded HTML or SVG, path traversal, files readable by the wrong people), so the design starts from what must never happen.

## Decision
- **Files belong to a document.** The Core DocType `File` records the name, size, detected type, SHA-256 and which document it belongs to (and a storage key at permission level 9, which the API never returns). The bytes live behind `IFileStorage`; the real implementation (`DiskFileStorage`) keeps them under `Qahwa:Files:Path` (default `data/files` next to the server) in files named by **random 32-hex keys, validated on every use**, so no path is ever built from anything a person typed.
- **Permissions on every request.** Uploading needs read and **write** access to the document; downloading and listing need read access, checked each time through the document engine (so own-only roles, shares and User Permissions apply). Someone who may not read the document is told the file "does not exist" rather than "forbidden". Removing a file needs write access to the document, or being the uploader or a System Manager. A document can hold 50 files; a file is at most 25 MB (`Qahwa:Files:MaxMegabytes`, 1 to 100) and not empty; the stream is counted as it is copied and a file over the limit is refused and its bytes deleted.
- **Uploads are raw bytes with the name in a header** (`X-File-Name`, percent-encoded). A custom header cannot be sent cross-site without a CORS preflight, so another site cannot upload on someone's behalf (the cookie is also SameSite=Lax).
- **Safe by type.** The name is cleaned (folders, control characters and `" < > : | ? *` removed, length limited, extension kept). The type comes from the **content**, never from the name or the client's claim: only real PNG, JPEG, GIF and WebP pictures (by their first bytes) are ever shown inline; everything else, including HTML, SVG and PDF, is sent as `application/octet-stream` with `Content-Disposition: attachment`. Every download also carries `X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'; sandbox` and `Cache-Control: private, no-store`.
- **Lifecycle.** The activity timeline notes "Attached x" and "Removed x". Deleting a document deletes its files (rows and bytes); renaming or merging a document moves the references. A file that is refused never stays on disk.
- **Desk.** An Attachments card in the side panel of a saved document: choose or drop files, thumbnails for pictures, download or open, remove; empty and oversized files are refused before sending, and the server checks again.

## Security
Nothing a person types becomes a path; the stored name is random. Content is never interpreted: pictures are recognised by magic bytes and everything else is an opaque download, so uploaded scripts cannot run in the application's origin. Access is checked against the document on each request and unreadable files look absent. Tests cover hostile names (`../..`, drive letters, CR/LF header injection), HTML, SVG and fake JPEGs, limits, permissions for each action, deletion with the document, and the response headers on the real HTTP pipeline.

## Consequences
- Not yet: virus scanning, thumbnails generated on the server, per-user quotas, "attach" fields that store one file in a field, S3 or Azure Blob storage (the interface is ready), resumable uploads, and a File Manager page (the Core File list serves as one for System Managers).
- Files are not included in database backups: back up the files folder with the database.
