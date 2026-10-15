# ADR-015: Assigning, following and in-app notifications

**Status:** accepted (L7, part 2)

## Context
Comments and a history are only useful if people find out about them. People also need to hand a document to someone ("please review this") and to keep an eye on things they care about.

## Decision
- **Three ordinary DocTypes** (`ToDo`, `Document Follow`, `Notification Log`), written by services, read-only through the generic API, with no updates log of their own. The services are `AssignmentService`, `FollowService` and `NotificationService`; `CollaborationHooks` is what the document engine and comments call.
- **Assigning** a document needs the **same right as sharing it** (read it, and edit it through roles), the assignee must be an existing, enabled user, and if they cannot open the document yet they are **given read access through a share** so the task is not a dead link. Assigning the same person twice changes nothing. Description (500 characters of plain text), due date (`yyyy-MM-dd`) and priority (Low, Medium, High) are validated. The assignee, the person who assigned, or an Administrator can mark it done or remove it; anyone else gets "not found", so assignments are not visible by guessing ids. A system note in the comment thread records assigning, completing and removing.
- **Following.** You follow what you **create, comment on or are assigned**, and can follow or unfollow any document you can read. Followers are told when someone else changes the document or comments on it (at most 50 followers are processed).
- **Notifications** are one row per person per event (`Mention`, `Assignment`, `Comment`, `Update`, `Share`). **Before a row is written** the recipient must exist, be enabled and be allowed to read the document, judged by the real document engine with their roles; otherwise nothing is written. So a notification, and the comment excerpt in a mention, can never reach someone who could not open the document. Nobody is notified about their own action. You can only list and mark read your own; someone else's id is "not found".
- **@mentions** are `@email` tokens (at most 10 per comment). Mentioned people get the comment text as an excerpt (after the readability check); other followers get "X commented on ..." without the text.
- **Clean-up.** Deleting a document deletes its assignments, followers and notifications; renaming or merging repoints them in the same transaction.
- **Desk.** A bell with the unread count (checked every 30 seconds) and a panel to open or mark notifications read; an Assigned to panel and a Follow button on each saved form; "Assigned to me" on the home page.

## Security
Every list and mutation is scoped to the signed-in user or gated by read access to the document; a notification is never created for someone without access; assignee, due date, priority and text are validated and length-limited; text is rendered as text in the Desk.

## Consequences
- Notifications are in-app only for now; email and push, digests and per-user preferences arrive with the notification system (L8).
- Not yet: assignment rules (auto-assign), reminders for due dates, following by tag, and unfollowing everything at once.
