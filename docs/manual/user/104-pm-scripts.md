# Scripts for Project Management

Scripts let a System Manager add small rules and helpers in the Desk, with no developer. This page installs six ready-made ones for [Project Management](103-project-management.md). Each is a file in the repository under `apps/Qahwa.ProjectManagement/Scripts/` (ask your developer for the text, or open it on GitHub). The [developer chapter](../developer/13-server-and-client-scripts.md) explains how they work.

| What you get | Kind |
|---|---|
| A **Tasks** section on each project listing its tasks, with *OVERDUE* marked | Server Script, After Save on Pm Task |
| A daily **email** to the person of each task that is due today | Server Script, Scheduled, Daily |
| An address that answers with a project's overdue tasks | Server Script, API `project_overdue` |
| A refusal to log more than 12 hours a day for one person | Server Script, Validate on Pm Time Log |
| A banner on the project (progress and hours, or over budget), and a refusal to complete a project with open tasks | Client Script on Pm Project |
| An *overdue* banner on a task, a **Mark as done** button, and a person required while it is in progress | Client Script on Pm Task |

## Install a Server Script
1. Open **Server Script** and click **New**.
2. Type a **Title**. Choose **When** and the **DocType** from the table above (for the daily email, *When* is **Scheduled** and *How often* **Daily**; for the API, *When* is **API** and *API method name* is `project_overdue`).
3. Paste the file's text into **Script**, make sure **Active** is ticked, and **Save**.
4. If the script has a mistake you are told at once. If it fails later, **Last problem** on the script says why.

## Install a Client Script
Open **Client Script**, click **New**, choose the **DocType** (`Pm Project` or `Pm Task`), paste the text and save. Reload a project or task to see it.

## What to expect
- Save a task: open its project and the **Tasks** section is up to date.
- The daily email is first **scheduled** when the scheduler sees it, then runs about once a day. It runs as **you**: you must be able to read the tasks. Each message goes to the **Assigned to** person (they need an account here), is listed in **Email Queue** (sidebar, Core) and is sent if the server has an email server set up (otherwise it is only logged). At most 20 emails go out per day by this script; for more, or no code at all, use a [notification rule](12-notification-rules.md): *Pm Task, Days before a date, Due date, 0 days, tick Also send an email, tell the field Assigned to*.
- The project's **Tasks** box is not refreshed when a task is **deleted**; it updates the next time one of its tasks is saved.
- A client script can only change what the form shows. Rules that must always hold are enforced on the server (the app does that for the progress numbers, hours and closed projects).
