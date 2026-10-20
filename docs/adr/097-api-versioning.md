# ADR-097: URL versioning of the API

Status: accepted

## Context
Programs written against the REST API need a promise: an incompatible change will not appear under the address they already use.

## Decision
- `/api/v1/…` is the same API as `/api/…`. A middleware (`UseQahwaApiVersion`) rewrites the path before routing, so every rule (sign-in, permissions, rate limits, network restriction) applies unchanged and nothing is duplicated.
- Every `/api` response carries `X-Qahwa-Api-Version: 1`.
- An incompatible change will be offered as `/api/v2/…` while `/api/v1/…` keeps working; an unknown version (`/api/v2/…` today) is simply not found. Un-versioned `/api/…` stays an alias for the current version.

## Consequences
- New integrations should use `/api/v1/`. The Desk keeps `/api/`.
- Only the path prefix is rewritten; there is no negotiation by header.
