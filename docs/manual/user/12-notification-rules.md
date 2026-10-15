# Notification rules: tell people automatically

A **notification rule** tells people when something happens to a document, without anyone writing to them: a large claim was submitted, an order was marked "Shipped", a contract ends in a week. People see the message in their **notification bell**. Rules are set up by a System Manager in **Notification Rules** (Tools in the sidebar).

Nobody is told about a document they are not allowed to open, whatever the rule says.

## Setting up a rule

1. Open **Notification Rules** and choose the DocType.
2. Click **Add rule** and name it.
3. Choose **When**:
   - **A document is created**, **changed**, **submitted** or **cancelled**.
   - **A field changes**: choose the field. Other changes do not count.
   - **Days before a date** or **Days after a date**: choose the date field and the number of days. For example "3 days before Due date" tells people three days ahead, once a day.
4. Optionally fill **Only when**, for example `amount > 1000` or `status == 'Urgent'`.
5. Tick **Also send an email** if the message should go out by email as well (see [Email](13-email.md)).
6. Choose **who is told**: people (search by name or email), everyone with a role, the people named in a field of the document (for example "Sales rep"), the person who created it, everyone following it. The person who made the change is never told about their own change.
7. Write the **Message**, for example `{{user}} set {{title}} to {{status}}`. Use **Insert a field** to add a field's value. `{{user}}` is the person who did it, `{{name}}` the document's ID, `{{doctype}}` its kind.
8. **Save rules.** They apply from then on.

## Good to know

- Rules for dates are checked about once an hour, and each person is told once per rule and document per day.
- "A document is changed" only fires when a saved change really altered something.
- Messages are one line of plain text of up to 300 characters; long values are shortened.
- Several rules can tell the same person; they get one notification per rule.
- Switch a rule off with **Active** to keep it without it firing.
