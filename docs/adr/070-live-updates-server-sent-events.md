# ADR-070: Live updates with Server-Sent Events

Status: accepted

## Context
The bell checked for notifications every 30 seconds, and a form never learned that someone else had changed the document until a save failed with a conflict. The plan named SignalR.

## Decision
- **Server-Sent Events, not SignalR.** One `GET /api/realtime` stream per browser tab uses plain HTTP, works with the cookie session and the existing permission model, needs no extra package or client library and no WebSocket support from proxies. The browser reconnects by itself. SignalR can replace it later behind `IRealtimePublisher` if a two-way channel is ever needed.
- **Events say where, never what.** `{ type: "notification" }` for a person, and `{ type: "doc", doctype, name, user, deleted }` for a document. No field values are ever sent; the Desk asks for the data with its normal, permission-checked requests.
- **Who gets what.** A stream opened with no document receives only its own person's notification events. A stream opened with `?doctype=&name=` receives changes to that one document, and only if the person can read it at the moment they connect (`DocumentService.GetAsync`, so a missing and a forbidden document look alike as 404/403). API-key requests are refused: live updates are for signed-in sessions.
- **Limits.** At most 6 streams per person and 5,000 per server (more get 429); each stream has a queue of 64 events and drops the oldest when its reader is slow, so a stalled browser cannot grow the server's memory; a comment line is sent every 20 seconds to keep proxies from closing a quiet stream.
- **Where events come from.** `DocumentService` announces an insert, update, submit, cancel, workflow change or delete (after the database change), and `NotificationService` announces a new notification to its recipient. Both take an optional `IRealtimePublisher`; `RealtimeHub` is the in-memory implementation registered per site.
- **Desk.** The bell updates as soon as a notification arrives (its polling is now a slow 5-minute fallback). An open form refreshes when someone else saves it and the form has no unsaved edits; with unsaved edits it shows a warning with "Reload latest" and never replaces the person's work; a delete is announced. A person's own saves are ignored.

## Consequences
- The hub lives in the memory of one server. With several servers behind a load balancer, a person only hears changes made on the server their stream is connected to, until a shared backplane (Redis or the database) is added behind `IRealtimePublisher`.
- Each open form holds one connection; browsers allow about six per site on HTTP/1.1 (no limit in practice on HTTP/2).
- Comments, assignments and attachments do not announce themselves yet; the form shows them after its next refresh.
