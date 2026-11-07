# Project Management

**Project Management** is an app for planning work: projects, the tasks inside them, and the time people spend. It is installed on this site when the **Project Management** module appears in the sidebar and **Tools → Apps** lists it. If it is missing, ask your administrator to switch it on (it is off until a site's settings say `Qahwa:Apps:ProjectManagement = true`).

## Who can do what

| Role | What it can do |
|---|---|
| **Project User** | See projects, edit them, and create and edit tasks and time logs |
| **Project Manager** | Everything above, plus create and delete projects, delete tasks, export and run reports |

Give people the role in their User document.

## Projects
A project has a name, a **status** (Planning, Active, On Hold, Completed, Cancelled), a manager, start and end dates and a **budget in hours**. The end date cannot be before the start date. Projects open as a **board** with a column per status.

Two numbers are worked out for you and cannot be typed:
- **Complete (%)**: the share of the project's tasks that are Done.
- **Logged (hours)**: the total of all time logged on its tasks. Compare it with the budget.

Once a project is **Completed** or **Cancelled**, no new task can be added to it.

## Tasks
A task has a subject, its project, a **status** (Open, In Progress, Review, Done), a **priority** (Low to Urgent), who it is **assigned to**, a **due date** and an estimate in hours. Tasks also open as a board: drag a card to another column to change its status.

- When a task becomes **Done**, today's date is kept as **Completed on**; reopen it and the date is cleared.
- A task that is not Done and past its due date counts as **overdue**.
- Use **Assign to**, comments with @mentions, **Remind me** and tags on a task like on any document.

## Time logs
Open **Pm Time Log** and add an entry: the task, the date (today by default), the **hours** (more than 0, at most 24) and what was done. It belongs to you. The task's and the project's **Logged (hours)** update straight away, and again if you change or delete the entry.

## Seeing where things stand
- **Script Reports → Project status**: one row per project with its status, manager, end date, number of tasks, how many are overdue, percent complete, the budget and the hours logged. You see the projects you may read.
- Developers and other systems can ask for one project's summary (`project_management.summary`: tasks by status, overdue count, progress, hours) through the API; see [Building an app from scratch](../developer/12-building-an-app-from-scratch.md), which uses this app as its example.
