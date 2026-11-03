# ADR-143: Text blocks on workspaces

**Status:** accepted (Desk)

## Context
Workspaces (ADR-053) had headings, shortcuts and dashboards. A team also wants words on the page: a welcome, how to file a request, who to ask. Frappe offers a Custom HTML Block that runs whatever HTML and script an administrator writes; that is an XSS surface inside the Desk, where every other signed-in person's session lives.

## Decision
- A workspace gets a fourth block type, **Text**: formatted text edited with the same rich-text editor as Text Editor fields. It is not HTML you author; it is bold, italics, lists, links and the like.
- **Cleaned three times.** The server cleans it with `HtmlSanitizer` when the workspace is saved (what is stored is clean), again when it is sent to a reader (what is stored is never trusted to still be clean), and the Desk cleans it once more before showing it. Scripts, styles, event handlers and unsafe addresses never survive any of these.
- The block needs some visible text and may be at most 5000 characters long, formatting included.
- No other change: who sees a workspace, block order and the 30-block limit are as before.

## Consequences
- A team cannot embed a widget, a form or a script on a workspace; for data use a dashboard, for actions a shortcut, for a form a Web Form.
- Not yet: a page builder, images in text blocks (links only), and per-block visibility.
