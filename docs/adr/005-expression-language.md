# ADR-005: A small expression language for field logic

**Status:** accepted (L4)

## Context
Low-code needs conditions ("show this field only when…"), calculations (`qty * rate`), defaults and rules, written by the person designing the DocType, without scripts. The same rule must give the same answer in the form (while typing) and on the server (when saving), and must be safe to run on the server for any user's input.

## Decision
- **One language, two implementations.** A hand-written parser and evaluator in C# (`Qahwa.Core.Expressions`) and in TypeScript (`src/lib/expr`). Both read the same vectors (`tests/expression-vectors/vectors.json`, 220+ cases: results, edge cases and parse errors) in their test suites, so they cannot drift apart unnoticed.
- **Safe by construction.** No loops, assignment, user-defined functions, reflection or access outside the supplied fields. Source length (1000), nesting depth (40) and node count (400) are limited. Evaluation never throws: operations on the wrong kind of value give no value or false.
- **Defined, not borrowed, semantics.** Equality is strict, `&&`/`||` give booleans, numbers are IEEE doubles on both sides, case changes only touch ASCII, `round` is half away from zero computed identically, and dates are ISO text with a shared day-count algorithm. Where JavaScript and C# disagree natively, the language picks one behaviour and both implementations implement it.
- **Checked when the DocType is saved**, not when it is used: syntax, unknown functions and argument counts, unknown field names, `parent` outside child tables, circular formulas, `fetchFrom` targets and their types. Errors point at the offending property (`fields[3].formula`) and character.
- **The server is authoritative.** Formulas are recalculated on save and override what a client sends. The Desk runs the same logic for live feedback: calculated values are derived on every render from what the user typed, never stored, so they always follow their inputs.
- **Conditions skip validation, not data.** A field that is not shown is not required; its value, if any, is still type-checked and kept.
- **`fetchFrom` respects permissions.** A value is only copied when the person may read the linked document, so a field cannot be used to read data the person could not otherwise see. It copies when the link is new or changed (a snapshot), not on every save.
- **Updates leave no stale numbers.** When a document with formulas is updated, saved rows the client did not send are loaded and recalculated too (rows can read the document as `parent`), and only the values that really changed are written.

## Consequences
- Adding a function means implementing it twice and adding vectors; the test suites fail until both agree.
- `readOnlyDependsOn` and `hidden` stay UI hints, like Frappe. Server-enforced read-only is a later step.
- Formulas cannot read other documents or run queries (that needs scripts, planned for L11).
- The language documentation is `docs/expressions.md`.
