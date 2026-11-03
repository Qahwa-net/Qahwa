# ADR-149: Azure Blob Storage for attachments

**Status:** accepted

## Context
Attachments could live on disk or in an S3-compatible bucket (ADR-145). Organisations on Azure keep their files in Blob Storage, which does not speak S3.

## Decision
- **A third `IFileStorage`**: `Qahwa:Files:Provider = azure` with `Qahwa:Files:Azure:{Account, Key, Container, ServiceUrl, Prefix}`. `ServiceUrl` is optional (default `https://<account>.blob.core.windows.net`); it exists for Azurite and sovereign clouds and may carry the account as a path, as Azurite does.
- **Shared Key authorization written out** (`AzureSharedKey`, HMAC-SHA256 over the documented string to sign), so no SDK is needed, as with SigV4. Version header `2021-08-06`.
- **Same object layout as disk and S3:** block blobs named `[prefix/]ab/<32 hex key>`; keys are validated before any request is built.
- **A save is one Put Blob** from a temporary file, so the length is known and nothing large is held in memory (the existing 100 MB ceiling keeps it inside a single Put). Reads stream; a 404 is `FileNotFoundException`; deleting what is gone is not an error; any other status is an `IOException` naming the operation and status (never the key or the response body).
- **Settings are checked at start-up**: account and container names, a base64 key, https unless the service is on localhost, and a safe prefix. A wrong setting stops the server with the name of the setting.

## Consequences
- Verified for real: the Azurite emulator accepted the signature, a 300 KB file round-tripped byte for byte, a read after delete was "not found", and a wrong key was refused (`QAHWA_TEST_AZURE_URL`; skipped without it). Unit tests cover the string-to-sign layout, the request shape, error mapping and option validation.
- Not yet: shared access signatures or managed identity (Entra ID) instead of an account key, blobs over 100 MB (block upload), Google Cloud Storage.
- Like S3, switching an existing site does not move the files already saved; the container must exist and should be private.
