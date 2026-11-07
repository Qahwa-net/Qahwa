# ADR-165: A Settings group in the sidebar, and the framework's DocTypes in the search

**Status:** accepted

## Context
A System Manager could not find where to configure email. The Email Account page existed, but it sat in the **Tools** group, which starts folded, holds about 23 entries and is last but one in the sidebar; and the search box (Ctrl+K) left out every DocType of the *Core* module (Email Account, Email Queue, User, Error Log, Server Script...) and several tool pages, so typing "email" found nothing useful.

## Decision
- **A Settings group** for the configuration pages, shown to System Managers and open by default, ranked right after *My work* and *Workspaces*: **Email Account, Email Template, Email Queue, Brand, Naming Series, Workspaces, Role Permissions, Backup & Restore** (the last for Administrators). Other tools stay in Tools. Link ids do not change, so saved layouts (pinned, hidden, order) keep working. The group is listed only for people who can manage; others do not see it.
- **The search box finds the framework's own DocTypes.** They are listed after your own DocTypes' matches and only once two letters are typed (so one letter does not fill the list with system names), and users see only what they may read, as before.
- **Search keywords:** an entry can carry related words that are matched after its name ("smtp", "imap", "mail" find Email Account; "cron" finds Scheduled Jobs; "logo" finds Brand; "password" finds User).
- **More pages in the search:** Developer Hub, Scheduled Jobs, Script Reports, Energy Points, Emailed Reports, Apps, Sites, Slow requests, OAuth Clients.
- **Developer Hub** lists Email Account. A user guide page, *Set up email*, says where it is and walks through SMTP and IMAP with provider settings and fixes.

## Consequences
- Tested: the settings group for managers and not for employees (API test); group rank and fold, and search by name and keyword (Desk tests). Seen in the running app: the group below the workspaces, and "email" listing Emailed Reports, Email Account, Email Digest, Email Queue and Email Template.
- Not done: a Settings workspace page with cards, and a first-run checklist that tells a new administrator to set up email.
