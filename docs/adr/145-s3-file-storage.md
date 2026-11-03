# ADR-145: Attachments in an S3-compatible bucket

**Status:** accepted (integrations)

## Context
Attachments lived on the server's disk behind `IFileStorage`. That ties files to one machine, which does not suit a container that is replaced, several servers behind a balancer, or a small disk. Most hosts (Amazon, Cloudflare R2, Wasabi, MinIO, Contabo) offer an S3-compatible bucket.

## Decision
- `S3FileStorage` implements `IFileStorage` over HTTP with **AWS Signature Version 4** written out in `SigV4` (about forty lines on `System.Security.Cryptography`), so no SDK is added to the server. It is chosen with `Qahwa:Files:Provider = s3` and configured by `Qahwa:Files:S3:*` (service URL, bucket, region, keys, optional prefix, path or virtual-host style); the default stays the disk.
- **Same safety as the disk storage.** A key is 32 random hex digits, checked on every use, so a name from a person can never address another object or climb out of the prefix. The code never logs or throws a key, secret or signature; an error says only the operation and the HTTP status.
- **Settings are checked at start-up** (`S3Options.Validate`): a real URL with no path, https (plain http only for localhost, for testing), a valid bucket name, both keys, a region, and a safe prefix. A mistake stops the server with a message that names the setting, instead of failing on the first upload.
- **A file is copied to a temporary file while its SHA-256 is computed**, then sent in one signed PUT with that hash, so nothing big is held in memory and the signature covers the content (no `UNSIGNED-PAYLOAD`, which some services refuse over http). Reads stream the response and let go of it when the reader is done. A missing object is a `FileNotFoundException` (as on disk) and deleting a missing one is not an error.
- **Verified two ways.** Unit tests reproduce the two signatures AWS publishes in its Signature V4 documentation (a GET with a Range header, and a PUT with a name that needs encoding), and check the request that is sent (address, headers, hash, path and virtual-host style, prefix, errors without secrets, bad keys and bad settings). A test that runs only when `QAHWA_TEST_S3_*` environment variables point at a server did a real round trip (300 KB of random bytes saved, read back identical, deleted, then not found, and a wrong secret refused) against SeaweedFS, an S3-compatible server that verifies signatures.

## Consequences
- The bucket should be private: Qahwa checks permissions on every download, and a public bucket would let anyone with a link skip them.
- Moving an existing site from disk to a bucket does not copy the files already saved (copy `data/files` into the bucket under the same names, in sub-folders by the first two characters). Backups (ADR-123 and neighbours) still leave file contents out, as before: use the bucket's own versioning or replication.
- Not yet: Azure Blob and Google Cloud Storage, a one-off command that copies disk files into the bucket, signed download links that let the browser read from the bucket directly, multipart upload for files over 5 GB (the attachment limit is 100 MB), and keeping the Backup archive itself in a bucket.
