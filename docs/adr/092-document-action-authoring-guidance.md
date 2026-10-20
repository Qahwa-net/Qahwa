# ADR-092: Guidance and stricter checks when writing Document Actions

Status: accepted

## Context
Document Actions (ADR-035) take free text for the field to watch, the field to set and the mapping of a created document. Typos were found only when the action ran, and an action for Submit or Cancel on a DocType that cannot be submitted never ran at all.

## Decision
- **The server refuses more at save time** (`DocumentActionEngine`): Submit and Cancel events only for submittable DocTypes (the message points to ValueChange or New/Update), every mapping line must read `target_field = value`, and a target field may be mapped only once. The existing checks (the fields exist, the target is an ordinary DocType, placeholders are known, at most the allowed number of lines) are unchanged.
- **The form helps while writing.** On the Document Action form, `DocumentActionFields` reads the source and target DocType definitions the person may read and shows: the fields that can be watched or set (one click fills the box), the placeholders available from the existing document (`{{field}}`, `{{name}}`, `{{doctype}}`, `{{user}}`), and for Create Document a list of the target's fields with an example line for each (a matching field is mapped to the same placeholder, a link to the source gets `{{name}}`, otherwise a literal from the default, a Select option or a sample), plus warnings when the event cannot work or when the target has a required child table the action cannot fill. Clicking an example appends it; nothing is saved by the helper.

## Consequences
- Checks live on the server, so actions made through the API are held to the same rules as those made in the form.
- Actions saved before these checks that break them are refused the next time they are edited and saved, and show "Last problem" when they run.
