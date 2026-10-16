# ADR-072: Email digest

Status: accepted

## Context
The bell (ADR-015) only helps people who open the Desk. Many want one email that says what needs them.

## Decision
- **Opt-in, per person, to their own address only.** Each person turns on a Daily or Weekly digest and picks an hour (UTC) in their account page; `GET/PUT /api/email-digest` read and change only the caller's own settings (`Email Digest` row named after the person's email). There is no way to set up a digest for someone else, so nobody can be made to receive one.
- **Content:** the person's unread notifications (the latest 30) and open assignments (up to 20 shown, the rest counted), built with the person's own permissions (`NotificationService.ListAsync` and `AssignmentService.MineAsync` as them). Text from other people (notification subjects, assignment notes) has control characters replaced and is cut to 200 characters per line, so it cannot add email headers or flood the message.
- **Nothing to say, nothing sent.** If there are no unread notifications and no open assignments, no email is queued for that occurrence.
- **Same scheduling guarantees as emailed reports (ADR-071):** each occurrence is claimed with a compare-and-swap before sending, and a missed period sends one digest, not one per missed day. A person whose account has been disabled is skipped.
- The email is plain text, queued in the ordinary email queue.

## Consequences
- The email names documents but carries no links (the system does not know its public address yet); the person opens the Desk.
- The hour is UTC, not the person's local time.
- Unread notifications stay unread after a digest; the digest does not mark them read.
