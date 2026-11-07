// Server Script   When: Scheduled   How often: Daily
// Emails the person each task is assigned to when it is due today and not finished.
// It runs as the person who made the script, so that person must be able to read the tasks.
// qahwa.email(to, subject, message, doctype, name) queues a message for a person who has an account here.

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
