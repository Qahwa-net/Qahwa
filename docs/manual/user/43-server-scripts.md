# Server Scripts

A **Server Script** is a few lines of JavaScript that run on the server when documents of a DocType are saved. Use one when a rule is too special for a formula or a Document Action. Only a **System Manager** can make them.

Open **Server Script** and create one:
- **DocType**: which documents it is about.
- **When**:
  - **Validate**: before a document is saved (not when it is cancelled). It can change fields and can refuse the save.
  - **Before Submit**: before a submittable document is submitted. It can change fields and refuse.
  - **Before Cancel**, **Before Delete**: it can refuse the cancellation or deletion.
  - **After Save**, **After Submit**, **After Cancel**: after it happened. They can look at and create other documents.
- **Script**: the code. It is checked when you save.

## What you can use
| | |
|---|---|
| `doc` | the document's fields, e.g. `doc.amount`; assign to change one (Validate only) |
| `old` | the document before this change (`null` for a new one) |
| `isNew`, `user.name`, `user.roles` | is it new; who is saving |
| `qahwa.error("text")` | refuse the save with this message |
| `qahwa.log(...)` | write a line you can see when you try the script |
| `qahwa.today()`, `qahwa.now()` | today's date and the current time (UTC) |
| `qahwa.get("Customer", "ACME")` | one document, or `null` if there is none |
| `qahwa.list("Order", { fields: ["name","total"], filters: [["customer","=",doc.customer]], limit: 20 })` | a list of documents |
| `qahwa.insert("ToDo", { description: "Call " + doc.customer })` | create a document (After events only) |
| `qahwa.update("Order", "SO-0001", { status: "Closed" })` | change a document (After events only) |
| `qahwa.email("ann@example.com", "Subject", "Message", "Order", "SO-0001")` | queue an email to a person who has an account here; the last two (a document you may read) are optional. After events, API and Scheduled scripts only; at most 20 a run |

Examples:

```js
// Validate on Order: block big orders from customers who owe money
var open = qahwa.list("Invoice", { filters: [["customer", "=", doc.customer], ["status", "=", "Unpaid"]], fields: ["amount"] });
var owed = open.reduce(function (sum, i) { return sum + i.amount; }, 0);
if (owed > 5000) qahwa.error("This customer owes " + owed + ". Ask finance first.");
```

```js
// After Save on Ticket: make a follow-up task
if (isNew && doc.priority === "High") qahwa.insert("ToDo", { description: "Call about " + doc.name });
```

## Can a script read the database?
Yes, documents: `qahwa.get` and `qahwa.list` read them **as the person who saved**, so a script only ever sees what that person may open. There is no SQL and no access to anything else. For more, with worked examples (a task list on a project, a reminder email, an API of your own), see [Server and client scripts](../developer/13-server-and-client-scripts.md).

## Rules to know
- A script **acts as the person who saved**: it can only read and write what they may. A script cannot reach files, the network or the server.
- It is **stopped** after about one second, too much work or memory, more than 20 reads or 5 writes. You will see a message saying so.
- A change made by a script does not start other scripts.
- If a **Validate** script has a mistake or fails, **nobody can save** that DocType until you fix or switch off the script; the problem is shown in **Last problem** on the script. A failing **After Save** script never undoes a save.
- Try a script before you rely on it: the Try endpoint (`POST /api/server-scripts/test`) runs it on a sample document and shows the changes and log without saving anything.
