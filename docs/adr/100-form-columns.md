# ADR-100: Columns in form sections

Status: accepted

## Decision
A **Column Break** field now starts the next column of the section it is in (up to four columns; empty columns are dropped). Without a column break a section keeps the two-column flow it had. `buildLayout` returns each section's `columns` as well as its flat `fields`; the form draws columns side by side and stacks them on a narrow screen. Tabs and collapsible sections already existed (ADR-072 and earlier). This is display only: a column break changes nothing about permissions, validation or the API.
