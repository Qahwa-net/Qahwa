# ADR-066: Password fields (encrypted at rest)

Status: accepted

## Context
Apps need to keep a secret on a document (the password of a mail account, an API key for another system). The Password field type existed in the list of types but was refused everywhere, so people stored secrets in plain Data fields.

## Decision
- A Password field holds text up to 1,000 characters. It is **encrypted before it is stored** with the same `ISecretProtector` (ASP.NET Core Data Protection, authenticated encryption) that protects two-factor secrets; the database never holds the plain text.
- **It never leaves the server in the clear.** A document read through the API shows a set password as the mask `********` and an empty one as nothing. Sending the mask back (as the form does for a field the person did not touch) leaves the secret as it is; sending text replaces it; sending an empty value clears it. The stored (encrypted) text is not returned anywhere.
- **Lists cannot reach it:** a Password field cannot be listed, filtered on or sorted by (the same refusal as a field above the person's permission level), and the default columns leave it out. It was already left out of export, import, reports, charts, print, webhooks, notification text, bulk edit, auto repeat and server scripts; the updates log records it as changed without a value.
- **Code that needs the secret** calls `DocumentService.GetPasswordAsync(doctype, name, field, user)`: it needs the right to change the document and to read and write that field, and it is not offered over REST. A value that cannot be decrypted (a changed key ring) gives nothing instead of an error.
- Password fields are allowed on top-level DocTypes only (a child table cannot have one) and are not offered as Custom Fields.
- Desk: a password input (never auto-filled by the browser's saved passwords).

## Consequences
- With several servers the Data Protection key ring must be shared (as for two-factor secrets), or a secret saved on one cannot be read on another. Losing the keys loses the secrets, by design.
- The mask is a fixed string: a person who really wants the password `********` cannot set it.
- Someone who may change a document can overwrite its secret but cannot read it through the API.
