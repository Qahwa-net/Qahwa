# ADR-163: A Project Management app, the worked example for app developers

**Status:** accepted

## Context
The developer guide described what an app can contain and how to install one, but the only complete example (CRM, ADR-146) is a sales tracker, and nothing walked through building an app from an empty folder. Developers asked for a start-to-finish example they could follow and compare with real code.

## Decision
- **`apps/Qahwa.ProjectManagement`** (app `project_management`) is a small real app: three DocTypes (*Pm Project*, *Pm Task*, *Pm Time Log*) as JSON files; hooks that refuse a task on a completed or cancelled project, stamp and clear the completion day, and roll a task's and a log's numbers up to the project; a server function `project_management.summary`; a script report `project_management.project_status`; and two roles (*Project User*, *Project Manager*) made by fixtures. `apps/Qahwa.ProjectManagement.Tests` has 13 tests.
- **Rolled-up numbers are stored, not computed on read**, so lists, boards and exports show them. They are worked out by after-hooks that act as the person who saved, so the roles that save tasks and logs have the rights the hooks need (write on the project), and the DocTypes that are added up are not restricted by owner.
- **Off until a site asks**, like CRM: registered only when `Qahwa:Apps:ProjectManagement = true`. It is on in `Properties/launchSettings.json` (for `dotnet run`; not in `appsettings.Development.json`, which the API tests also read and which would register the app without its DocTypes) and, by default, in `docker-compose.yml` (`QAHWA_APP_PROJECT_MANAGEMENT=false` turns it off), so a trial shows it on the Apps screen.
- **A developer chapter** (*Building an app from scratch: Project Management*) rebuilds the app step by step from `dotnet new classlib`, with the reasons for each choice, the tests, registration and installation, ideas for extending it, and the pitfalls met while writing it. A short user guide page describes the app.

## Consequences
- The example is real code under test, so the chapter cannot drift from something that works silently; changing the app means updating the chapter.
- Seen in the running app on PostgreSQL: the Apps screen lists it; a project, three tasks and a time log made through the API gave the right percent, hours and overdue count; a 30-hour log was refused; the task board showed the columns.
- Not done: per-project membership (who may see which project), notifications for overdue tasks, a Gantt view.
