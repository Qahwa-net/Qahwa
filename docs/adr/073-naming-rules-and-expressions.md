# ADR-073: Naming by expression and Document Naming Rules

Status: accepted

## Context
Names could come from a counter series, a field, a prompt or a random id. Real systems need more: a name built from several fields, and different series for different kinds of document (company contracts and personal contracts, big deals and small ones) without making a new DocType for each.

## Decision
- **`expression:<expression>`** is a new naming rule. The expression (the same safe language as formulas and conditions) is evaluated over the new document's fields, for example `expression:upper(region) + "-" + customer`, and the result, trimmed, is the name. It is checked when the DocType is saved (it must parse and may only read fields the DocType has). The name must be unique (a clash is reported as a conflict, not overwritten), at most 140 characters and free of control characters.
- **Document Naming Rule** is a Core DocType (System Manager only) with a DocType, a priority (lowest first), an optional condition (an expression over the document's fields), a naming rule (a series such as `CO-{YYYY}-.#####`, `field:<name>` or `expression:<…>`), and an enabled switch. When a document is named, the first enabled rule of its DocType whose condition holds (an empty condition always holds) decides the name; with no match, the DocType's own rule applies. A condition that cannot be evaluated is skipped, never an error. Each series prefix keeps its own counter.
- **Rules are checked when saved**: the DocType must exist and have documents with their own names (not child tables, single DocTypes or the rule DocType itself); the condition and naming rule must be valid for that DocType; `hash` and `prompt` are not allowed in a rule.
- **Hardening:** every name, whatever produced it (a field, a prompt, an expression), is now refused if it contains a control character such as a line break.
- **Desk:** the DocType designer offers "From an expression" and keeps the expression when a DocType is edited; rules are edited in the generic list and form of "Document Naming Rule".

## Consequences
- An expression name is not numbered: two documents that evaluate to the same text conflict, so include something unique.
- The series manager (ADR-063) lists counters used by rules without attributing them to a DocType.
- Rules are read for each new document (one small query); they are not cached.
