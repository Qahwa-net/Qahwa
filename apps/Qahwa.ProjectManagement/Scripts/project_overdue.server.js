// Server Script   When: API   API method name: project_overdue
// Call it:  POST /api/script-method/project_overdue   { "project": "PRJ-00001" }
// Answers with the project's tasks that are past their due date and not done. A read-only script: it only uses qahwa.list.

var today = qahwa.today();
var open = qahwa.list("Pm Task", {
  fields: ["name", "subject", "assigned_to", "due_date", "priority"],
  filters: [["project", "=", doc.project], ["status", "!=", "Done"]],
  orderBy: "due_date",
  limit: 50
});
var late = open.filter(function (t) { return t.due_date && t.due_date < today; });
result = { project: doc.project, today: today, open: open.length, overdue: late.length, tasks: late };
