// Server Script   DocType: Pm Task    When: After Save
// Keeps the "Tasks" box on the project up to date: one line per task, with its status, person and due date.
// It READS the database with qahwa.list (checked against the person who saved) and WRITES the project with qahwa.update.

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
