# ADR-079: Phone fields are checked

Status: accepted

## Context
A Phone field accepted any text, so "call me", spreadsheet formulas or markup could be stored in it and shown or exported later.

## Decision
- A phone value is trimmed and must be an optional leading `+` followed by digits, spaces, dashes, dots or brackets, with 4 to 15 digits and at most 25 characters. Anything else is refused with "Value must be a phone number, such as +966 50 123 4567." An empty value stays empty.
- The number is stored as typed (spacing is kept). Only fields that change are checked, so old values are not rejected until someone edits them.
- A country picker (dial-code list) is not part of this change.

## Consequences
- Extensions ("ext. 12") are not accepted; use a separate field.
