# Workflows: approvals and states

A **workflow** is the path a document follows: for example an expense starts as **Draft**, a clerk **requests** approval (**Pending**), and a manager **approves** it (**Approved**). Workflows are set up by a System Manager, in the **Workflow Builder** (Tools in the sidebar), and need no programming.

## Using a workflow

- Open a document: next to the title a coloured label shows where it is, with a button for each thing **you** may do with it ("Request", "Approve", "Reject").
- Click a button to move the document on. Save your edits first; the buttons work on the saved document.
- You cannot type the state yourself; only the buttons change it.
- If you see "Nothing for you to do here", someone else has to act, or the document has finished.
- **Waiting for my action** on the home page lists the documents you can act on now.
- Some states allow only certain people to edit the document, and some steps cannot be done by the person who created it. Some steps only appear when a condition holds (for example "amount is below 500").
- For documents that can be submitted, the workflow submits or cancels them for you; the usual Submit and Cancel buttons are hidden.

## Building a workflow (System Managers)

1. Make sure the DocType has a field for the state (a Data or Select field such as `workflow_state`), using the DocType Designer.
2. Open **Workflow Builder** and choose the DocType.
3. Choose the **State field**.
4. Under **States**, add each state with a colour. The **first** state is where new documents start. For submittable DocTypes choose the **document status** of states that submit or cancel. Fill **Roles who can edit** only if editing should be limited in that state.
5. Under **Transitions**, add each move: from state, the name of the action (the button text), the state it leads to, the roles who may do it, and optionally a condition. Untick **Creator may** to stop people approving their own documents.
6. **Save workflow.** It applies to documents straight away. Untick **Active** to switch it off, or **Remove workflow** to delete it; documents keep the state they are in.

If a row is wrong, the message appears under it. If someone else saved the workflow meanwhile, you are asked to reload.

**Tip:** a state nobody can leave (no transition from it) is an end state, such as Approved or Rejected.
