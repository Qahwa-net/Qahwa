# Milestones: how a status moved

A document often passes through steps: a ticket goes Open, In Progress, Closed; an order goes Draft, Packed, Shipped. **Milestones** show that path on the document, with who moved it and how long each step took.

## Turn it on for a DocType
A System Manager opens **Milestone Tracker**, adds a row, picks the **DocType** and the **field to track** (usually its status), and saves. There is one tracker per DocType. The field must be a plain value (text, number, date, choice, link): not a Password, a file, a signature or JSON. The DocType must keep its updates log (it does, unless it was switched off).

## Read it
Open any document of that DocType: above the activity there is **Status so far** (or the field's label), a row of steps, each with the value, when it began, who made the change and how long after the step before. The last one is the current value.

## Good to know
- Nothing extra is stored. The milestones are read from the document's history, so they are the real history. A change made before the DocType had a log is not there, and a DocType whose log is off has none.
- The first step is the value the document had when it was created, shown with the creation time and the person who created it.
- Only people who may read the document, and the tracked field, see milestones. If a field is restricted to some roles, others see no strip.
- Saving without changing the tracked field adds no step, and going from a value to the same value is not a move.
