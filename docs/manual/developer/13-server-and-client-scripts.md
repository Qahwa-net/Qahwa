# Server scripts and client scripts: a guide with Project Management examples

Scripts are small pieces of JavaScript that a **System Manager** writes in the Desk, with no build and no restart. They are for rules that are too special for a DocType property, a formula or a notification rule, but too small to justify an [app](08-writing-an-app.md). This chapter explains both kinds, answers the common questions, and uses the **Project Management** app ([chapter 12](12-building-an-app-from-scratch.md)) for working examples. The example scripts are real files in `apps/Qahwa.ProjectManagement/Scripts/`, covered by tests.

## The short answers

| Question | Answer |
|---|---|
| **Can a server script read the database?** | **Yes**, through `qahwa.get(doctype, name)` and `qahwa.list(doctype, { fields, filters, orderBy, limit })`. It reads **documents, as the person who triggered it**, so their permissions apply (a script never sees more than that person could open). There is **no SQL**, no raw table access and no other database or network by design |
| Can a server script write? | Yes: `qahwa.insert(doctype, values)` and `qahwa.update(doctype, name, values)`, checked, validated and logged like any save, as the same person. Not in Validate/Before events (they may only change `doc` and refuse) |
| Can it send email? | Yes: `qahwa.email(to, subject, message, doctype?, name?)`, to people who **have an account here** (ADR-164) |
| Can a **client script** read the database? | **No.** It runs in the browser in a sandbox that cannot reach the network, your session or the server; it only sees the fields of the form being edited and asks the form to do a few things. To show data from other documents on a form, let a **server script** write it into a read-only field (example 1) |
| Can a script call another script, run shell commands, read files or call a URL? | No |

## Which tool for which job

Use the least powerful tool that works:

1. **A DocType property**: required, default, `dependsOn`, `validations`, `formula`, `fetchFrom`. Runs in the form and on the server, no code.
2. **A notification rule** ([user guide](../user/12-notification-rules.md)) to tell people (bell and email) when something happens, including *N days before or after a date*. No code.
3. **A server script** for a rule that needs other documents, or an action after a save, on a schedule, or an API of your own.
4. **A client script** for a convenience on the form (a hint, show/hide, require, a button).
5. **An app** (C# hook, function, report) when it must be tested, versioned and shipped, or is bigger than a screen of code.

Rules that must always hold belong on the **server** (a client script can be bypassed with browser tools).

## Server scripts

### Create one

1. Sign in as a System Manager and open **Server Script** (sidebar, Core) and click **New**.
2. Give it a **Title**.
3. Choose **When** (the event) and, for a document event, the **DocType**:

| When | Runs | Can change `doc` / refuse | Can read | Can write / email |
|---|---|---|---|---|
| **Validate** | before every save except a cancel | change fields and `qahwa.error(text)` | yes | no |
| **Before Submit**, **Before Cancel**, **Before Delete** | before that step | refuse (Submit may also change fields) | yes | no |
| **After Save**, **After Submit**, **After Cancel** | after it is stored | no (use `qahwa.update`) | yes | yes |
| **API** | when someone calls `POST /api/script-method/<name>` | answers with `result` | yes | yes |
| **Scheduled** | hourly or daily, by the scheduler | no `doc` | yes | yes |

4. Type the **Script**, tick **Active** and **Save**. It is checked for mistakes when saved. For an API script also give the **API method name** (lower case letters, digits, underscores) and optionally **Who may call it** (a role). For a scheduled script choose **How often**.
5. Try it: `POST /api/server-scripts/test` (System Manager) runs a script on a sample document and returns the changes and the log without saving anything or sending email.
6. If something fails, **Last problem** on the script shows why. A failing *Validate* script stops saves of that DocType until you fix or switch it off; a failing *After* or *Scheduled* script never undoes anything.

### What a script can use

| | |
|---|---|
| `doc`, `old`, `isNew`, `event` | the document (fields only: no tables or passwords), the stored version before this change (`null` if new), whether it is new, the event name. In an API script `doc` holds the request body |
| `user.name`, `user.roles` | who is acting |
| `result = …` | API scripts only: what is sent back as `{ "data": … }` |
| `qahwa.get("Pm Task", "TASK-00001")` | one document or `null` |
| `qahwa.list("Pm Task", { fields: ["name","status"], filters: [["project","=",doc.project],["status","!=","Done"]], orderBy: "due_date", limit: 50 })` | up to 50 documents. A filter is `[field, operator, value]` |
| `qahwa.insert("ToDo", { description: "Call them" })` → name | create a document |
| `qahwa.update("Pm Project", "PRJ-00001", { status: "Active" })` | change a document |
| `qahwa.email(to, subject, message, doctype, name)` | queue an email (see below) |
| `qahwa.error("text")` | refuse the change |
| `qahwa.log(...)`, `qahwa.today()`, `qahwa.now()` | a log line (shown when you try it); today's date and the time in UTC |

Dates come back as text (`"2026-11-20"`), so `t.due_date < qahwa.today()` compares correctly. An empty field is `null`.

### Limits (they protect the server)

About one second, 100,000 statements, 8 MB of memory; **20 reads**, **5 writes** and **20 emails** per run; at most 50 rows per `qahwa.list`; 20,000 characters of script; 10 scripts per DocType and event; at most 50 scheduled scripts. A script acts **as the person who triggered it** (for a scheduled script, **the person who created it**, and it stops if that account is disabled). A change made by a script does not start other scripts or app hooks.

### Email from a script

`qahwa.email("ann@example.com", "Subject", "Message", "Pm Task", "TASK-00001")`

- The recipient must be a person with an **enabled account** on this site. A script cannot email strangers.
- The last two arguments are optional; when given, the acting person must be able to read that document, and the message is linked to it in the **Email Queue**.
- It counts against the acting person's **50 emails a day**, and 20 per run. Messages are plain text and go out like any other (see [Email](../user/13-email.md); without an SMTP server they are only logged).

### Example 1: show the tasks in the project (After Save on Pm Task)

Client scripts cannot read other documents, so a server script writes the list into a read-only **Tasks** box on the project. This is `Scripts/task_overview.server.js`:

```js
function overview(project) {
  var tasks = qahwa.list("Pm Task", {
    fields: ["name", "subject", "status", "assigned_to", "due_date"],
    filters: [["project", "=", project]],
    orderBy: "due_date",
    limit: 50
  });
  var today = qahwa.today();
  var lines = tasks.map(function (t) {
    var overdue = t.status !== "Done" && t.due_date && t.due_date < today;
    return (t.status === "Done" ? "[x] " : "[ ] ") + t.name + "  " + t.subject +
      "  (" + t.status + (t.assigned_to ? ", " + t.assigned_to : "") + (t.due_date ? ", due " + t.due_date : "") + ")" +
      (overdue ? "  OVERDUE" : "");
  });
  if (tasks.length === 50) lines.push("... only the first 50 tasks are shown.");
  qahwa.update("Pm Project", project, { task_overview: lines.length ? lines.join("\n") : "No tasks yet." });
}

overview(doc.project);
// A task moved to another project: the old project's box changes too.
if (old && old.project && old.project !== doc.project) overview(old.project);
```

Install it: **Server Script → New**, *DocType* `Pm Task`, *When* `After Save`, paste, Save. Open any project: the **Tasks** section lists its tasks. Two reads and two writes at most per save. It does not run when a task is **deleted** (there is no After Delete event); the app's own hook keeps the project's percentage right in that case, and the box refreshes the next time any of its tasks is saved.

### Example 2: email the task's owner when it is due (Scheduled, Daily)

`Scripts/task_due_reminders.server.js`:

```js
var today = qahwa.today();
var due = qahwa.list("Pm Task", {
  fields: ["name", "subject", "project", "assigned_to", "priority", "status"],
  filters: [["due_date", "=", today], ["status", "!=", "Done"]],
  limit: 50
});

var sent = 0;
for (var i = 0; i < due.length && sent < 20; i++) {          // at most 20 emails per run
  var t = due[i];
  if (!t.assigned_to) continue;                                // nobody to tell
  qahwa.email(
    t.assigned_to,
    "Task due today: " + t.subject,
    "Hello,\n\nThe task " + t.name + " (" + t.subject + ") in project " + t.project +
      " is due today and is still " + t.status + ".\nPriority: " + t.priority + "\n\nProject Management",
    "Pm Task", t.name
  );
  sent++;
}
qahwa.log("Reminders queued: " + sent + " of " + due.length + " tasks due today.");
```

Install it: **Server Script → New**, *When* `Scheduled`, *How often* `Daily`, paste, Save. A new scheduled script is only **scheduled** the first time the scheduler sees it, then runs about once a day at that time of day; runs missed while the server was off happen once, and several servers never run it twice. It runs as **you**, so you must be able to read the tasks.

**The no-code alternative, and when to prefer it.** A **Notification Rule** does the same without code and has no per-run cap: *Notification Rules → Pm Task → Add rule → When: Days before a date → Due date, 0 days → Only when `status != 'Done'` → tick Also send an email → Who is told: the field Assigned to → Message `{{subject}} is due`*. It also tells the person in the bell. Use the script when you need logic the rule cannot express (a different message by priority, escalation to the manager when it is late, creating a ToDo); use the rule for the plain reminder.

### Example 3: an API of your own (API)

`Scripts/project_overdue.server.js`, with *When* `API` and *API method name* `project_overdue`:

```js
var today = qahwa.today();
var open = qahwa.list("Pm Task", {
  fields: ["name", "subject", "assigned_to", "due_date", "priority"],
  filters: [["project", "=", doc.project], ["status", "!=", "Done"]],
  orderBy: "due_date",
  limit: 50
});
var late = open.filter(function (t) { return t.due_date && t.due_date < today; });
result = { project: doc.project, today: today, open: open.length, overdue: late.length, tasks: late };
```

```bash
curl -b jar.txt -X POST http://localhost:5101/api/script-method/project_overdue \
  -H "Content-Type: application/json" -d '{"project":"PRJ-00001"}'
# {"data":{"project":"PRJ-00001","today":"2026-11-07","open":3,"overdue":2,"tasks":[ ... ]}}
```

It reads as the **caller**: someone who may not read the project gets a permission error, not data. (For a function that ships with an app, a C# [server function](08-writing-an-app.md#server-functions-apimethod) is the better tool: typed, tested, versioned.)

### Example 4: refuse a save (Validate on Pm Time Log)

`Scripts/time_log_daily_limit.server.js`, *DocType* `Pm Time Log`, *When* `Validate`: no more than 12 hours a person a day.

```js
var person = doc.user || user.name;
var logs = qahwa.list("Pm Time Log", { fields: ["name", "hours"], filters: [["user", "=", person], ["log_date", "=", doc.log_date]], limit: 50 });
var total = doc.hours;
logs.forEach(function (l) { if (isNew || l.name !== doc.name) total += l.hours; });   // an edited log replaces itself
if (total > 12) qahwa.error("That makes " + total + " hours on " + doc.log_date + ". The most is 12.");
```

## Client scripts

A **Client Script** runs in the browser when a form (or list) of a DocType opens. Create one in **Client Script** (sidebar, Core): *DocType*, *Script*, *Active*.

```js
qahwa.ui.form.on('Pm Task', {
  refresh(frm) { /* after the form opens and after each save */ },
  status(frm)  { /* when the person changes the field "status" */ },
  validate(frm) { /* before saving: frm.throw('text') stops it */ },
});
```

| `frm` offers | |
|---|---|
| `frm.doc` | a **copy** of the form's fields (numbers are numbers; no passwords, files or tables) |
| `frm.set_value(field, value)` | fill a field |
| `frm.toggle_display(field, show)`, `toggle_reqd(field, required)`, `toggle_enable(field, enabled)`, `set_df_property(field, 'hidden' \| 'read_only' \| 'reqd', bool)` | show, require, lock |
| `frm.set_intro(text)` | the banner at the top of the form (up to 300 characters) |
| `frm.add_custom_button(label, function (frm) {…})` | a button on the form |
| `frm.msgprint(text)`, `frm.throw(text)` | a message; stop the save with a message |

List scripts use `qahwa.ui.list.on(...)` (see the [user guide](../user/67-list-scripts.md)). The sandbox is a Web Worker with the network, storage and page removed; a script that runs over a second is stopped. **It cannot read anything but `frm.doc`.**

### Example 5: the project form (`Scripts/project_form.client.js`)

```js
qahwa.ui.form.on('Pm Project', {
  refresh(frm) {
    var d = frm.doc;
    if (d.budget_hours && d.logged_hours > d.budget_hours) {
      frm.set_intro('Over budget: ' + d.logged_hours + ' of ' + d.budget_hours + ' hours used.');
    } else if (d.percent_complete != null && d.percent_complete >= 0) {
      frm.set_intro(d.percent_complete + '% complete' + (d.budget_hours ? ' · ' + (d.logged_hours || 0) + ' of ' + d.budget_hours + ' hours used.' : '.'));
    }
  },
  status(frm) { frm.toggle_reqd('end_date', frm.doc.status === 'Active'); },
  validate(frm) {
    if (frm.doc.status === 'Completed' && frm.doc.percent_complete != null && frm.doc.percent_complete < 100) {
      frm.throw('Finish the open tasks before completing the project (' + frm.doc.percent_complete + '% done).');
    }
  },
});
```

The percentage it shows was worked out by the app's hook, so the client script reads **stored** values; it does not query anything.

### Example 6: the task form (`Scripts/task_form.client.js`)

```js
qahwa.ui.form.on('Pm Task', {
  refresh(frm) {
    var d = frm.doc;
    var today = new Date().toISOString().slice(0, 10);
    if (d.status !== 'Done' && d.due_date && d.due_date < today) frm.set_intro('This task is overdue (due ' + d.due_date + ').');
    frm.toggle_reqd('assigned_to', d.status === 'In Progress');
    if (d.status !== 'Done') frm.add_custom_button('Mark as done', function (f) { f.set_value('status', 'Done'); });
  },
  status(frm) { frm.toggle_reqd('assigned_to', frm.doc.status === 'In Progress'); },
  validate(frm) { if (frm.doc.status === 'In Progress' && !frm.doc.assigned_to) frm.throw('Choose who is working on this task.'); },
});
```

## Install the whole set on a site

With the Project Management app switched on (`Qahwa:Apps:ProjectManagement = true`), as a System Manager:

| Where | Title | DocType / When | Paste the file |
|---|---|---|---|
| Server Script | Tasks box on the project | `Pm Task`, After Save | `task_overview.server.js` |
| Server Script | Email when a task is due | Scheduled, Daily | `task_due_reminders.server.js` |
| Server Script | Overdue tasks API | API, method `project_overdue` | `project_overdue.server.js` |
| Server Script | Daily hours limit | `Pm Time Log`, Validate | `time_log_daily_limit.server.js` |
| Client Script | Project form | `Pm Project` | `project_form.client.js` |
| Client Script | Task form | `Pm Task` | `task_form.client.js` |

Make a project and tasks: the project shows a banner, a **Tasks** section lists its tasks, an overdue task shows its banner and a **Mark as done** button, and a task due today queues an email to its person on the next scheduled run. (A script is data in the database, so it belongs to the site where you made it. To ship scripts with software, put the same logic in an app as a C# hook, as chapter 12 does.)

## Testing scripts

- **By hand:** *Try* (`POST /api/server-scripts/test`) for a server script; for a client script, reload the form.
- **In code:** a server script is data, so a test can insert it as a `Server Script` document and then save documents: see `apps/Qahwa.ProjectManagement.Tests/ProjectManagementScriptTests.cs` (it loads the files from `Scripts/`, runs them through the in-memory engine, and checks the project's box, the queued emails and the API answer). `tests/Qahwa.Core.Tests/ScriptEmailTests.cs` covers `qahwa.email`. For client scripts, `src/Qahwa.Desk/src/lib/exampleClientScripts.test.ts` runs the real sandbox worker on the example files.

## Pitfalls

| Problem | Cause and fix |
|---|---|
| Nobody can save the DocType after I added a Validate script | It has a mistake or runs out of limits: read **Last problem**, fix or untick *Active* |
| *A script may read at most 20 times* | A `qahwa.list` or `qahwa.get` inside a loop. Read once with a filter and loop over the result |
| *This script may not create or change documents* / *send email* | The event is a Before event or Validate. Do the write in an **After Save** script |
| My scheduled script did not run | It is scheduled the first time it is seen and runs after its period; it runs as its creator, who must be enabled; look at **Last problem** and **Scheduled Jobs** |
| *A script can only email people who have an account here* | The address is not an enabled User. Use a notification rule or an app for outside addresses |
| The script's change did not start my other script/hook | By design (no loops) |
| A client script cannot find data from another document | It cannot read any. Have a server script copy what you need into a read-only field |
| A value is a string where I expected a number | Date and Datetime fields are text; numbers are numbers; check with `typeof` or `Number(...)` |
