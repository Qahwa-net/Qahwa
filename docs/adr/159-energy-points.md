# ADR-159: Energy points

**Status:** accepted

## Context
Frappe gives people points for work done in the system (closing a ticket, being assigned and finishing) and shows a leaderboard. Used well it is a light way to recognise work that is otherwise invisible.

## Decision
- **Rules, set by System Managers** (`Energy Point Rule`): a DocType, an event (new, any update, a watched field changing, submitted, cancelled), an optional condition (the same expression language as Document Actions), a number of points (from -100 to 100, not zero), and which person gets them: `owner`, `modified_by`, or a Link-to-User field of the document. A rule can pay **once for each document** and at most **so many times a day** to one person (default 10), so points cannot be farmed by editing the same document.
- **A log nobody edits** (`Energy Point Log`): one row per award with the person, points, a reason naming the rule and the document, and the time. Only the server writes it; System Managers can read and delete rows. A person's total is the sum of their rows.
- **Awarded after the save**, next to Document Actions: an award never fails or undoes the save, never fires for energy point rules or log rows themselves, and does not trigger further awards. Nobody is rewarded who is not an enabled user of the site.
- **Leaderboard** for a week (from Monday), a month or all time, top 20, people with zero or fewer points left out. It shows **names only, never email addresses**, and marks the person asking. **My points** (total, this week, this month and the latest 20) are shown on the **Energy Points** page, a *My work* entry in the sidebar; each latest entry links to its document.

## Consequences
- Tested: awards by event and by condition, to a field-named person rather than the one who clicked, once-per-document and the daily cap, the leaderboard (names, ranking, "you", invalid period, signed-out), disabled rules and switched-off accounts, rule validation and the System Manager restrictions, and the API. Run for real on PostgreSQL: a rule on *Customer*, three customers, 15 points, the page showing the leaderboard and the entries (the sample rule, customers and log rows were deleted afterwards).
- Not yet: badges, points for comments, assignments and mentions (rules work on document events only), reverting points when a document is deleted, per-team boards, a points chip in the top bar.
