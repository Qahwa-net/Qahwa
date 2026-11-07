// Server Script   DocType: Pm Time Log    When: Validate
// Refuses a time log that would take one person past 12 hours in a day.
var person = doc.user || user.name;
var logs = qahwa.list("Pm Time Log", { fields: ["name", "hours"], filters: [["user", "=", person], ["log_date", "=", doc.log_date]], limit: 50 });
var total = doc.hours;
logs.forEach(function (l) { if (isNew || l.name !== doc.name) total += l.hours; });
if (total > 12) qahwa.error("That makes " + total + " hours on " + doc.log_date + ". The most is 12.");
