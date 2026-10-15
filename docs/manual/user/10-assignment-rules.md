# Assignment rules: documents that assign themselves

An **assignment rule** gives new documents to the right person without anyone doing it by hand: every new support ticket goes to the next person on the team, every urgent one to the duty manager, every order to the sales rep named on it. Rules are set up by a System Manager in **Assignment Rules** (Tools in the sidebar). No programming is needed.

## What people see

The person receives the document like any other assignment: a notification, an entry in **Assigned to me** on their home page, the document in their "Assigned to" panel, and a note in the document's Activity ("Assigned to Bob Stone by the rule "Support""). If they could not open the document before, they now can. They finish it with the tick (**Mark as done**) as usual.

## Setting up rules

1. Open **Assignment Rules** and choose the DocType.
2. Click **Add rule** and give it a name.
3. Choose how the person is picked:
   - **Round robin**: the people you list take turns.
   - **Load balancing**: the person with the fewest open assignments gets it.
   - **From a field**: the person named in a field of the document (for example "Sales rep").
4. Add the **people** (search by name or email) or choose the **field**.
5. Optionally fill **Applies when**: the rule only works for documents where this is true, for example `urgency == 'High'`. Leave it empty for every document.
6. Optionally fill **Withdraw the assignment when**, for example `status == 'Resolved'`: when that becomes true the assignment is taken back.
7. Optionally write what to tell the person, a **due after (days)** and a **priority**.
8. **Save rules.** They apply to documents saved from then on.

**Good to know**

- Rules are tried from the top; the **first** one that applies wins. Use the arrows to change the order, and switch a rule off with **Active** without losing it.
- A document is assigned by rules **once**. Saving it again, or finishing the assignment, does not assign it again.
- Disabled users are skipped. If nobody can be found the document is simply not assigned, and it is still saved normally.
- Existing documents are not assigned when you create a rule; a rule with a condition (such as `status == 'Escalated'`) starts working on a document the next time it is saved and the condition is true.
- Conditions use the same language as the rest of the Desk; `==` means "is equal to".
