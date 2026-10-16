# ADR-037: OpenAPI description of the REST API

**Status:** accepted (L12, part 5)

## Context
Integrators need to know which endpoints and fields exist. In Qahwa the DocTypes are data and can change at any time, so a hand-written description would always be out of date, and a complete one would reveal DocTypes and fields the caller may not know about.

## Decision
- **Generated, per caller.** `GET /api/openapi.json` builds an OpenAPI 3.0.3 document from the live DocType definitions **as the signed-in person (or API key) may use them**: only DocTypes they may read, only the fields they may read (a higher permission level hides a field), and only the operations their rights allow (create, change, delete, submit, cancel). It shows nothing the meta endpoint (`/api/meta/{doctype}`) does not already show the same person.
- **Schemas from field types.** Each DocType gets a read schema (with the standard fields name, owner, creation, modified, docstatus) and an `...Input` schema (writable fields only, `required` where the field is required and has no default). Select choices become enums, links say what they point at, tables reference the child schema, passwords are write-only. Schema keys are the DocType name with every character outside `A-Za-z0-9._-` replaced by `_`; paths use URL-encoded names.
- **Authentication** is described as a Bearer API key or the session cookie. Guests get 401. The response is `no-store`, `application/json` and `nosniff`.
- **Version.** The document's version is the API version ("1"). There is no URL versioning yet: the API is additive, and a breaking change will introduce `/api/v2`.

## Consequences
- Tools such as Swagger UI, Postman and client generators work against any installation, including its custom DocTypes.
- Not yet: the non-resource endpoints (workflows, print, import ...), examples, a built-in viewer page, URL-versioned APIs.
