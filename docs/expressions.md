# The expression language

Conditions, formulas, defaults and rules in the designer are written in one small language. It has no loops, no assignment and no way to reach anything except the fields in front of it, so it is safe to let users write it. The server and the Desk evaluate it identically (both are tested against the same 220+ cases in `tests/expression-vectors/vectors.json`).

## Where it is used

| Where | Property | Meaning |
|---|---|---|
| Field → Logic | **Show only when** (`dependsOn`) | The field is only shown while this is true. A field that is not shown is never required. |
| | **Required when** (`mandatoryDependsOn`) | Required while true, in addition to "Required". |
| | **Read-only when** (`readOnlyDependsOn`) | Cannot be edited while true (a hint in the Desk). |
| | **Calculate as** (`formula`) | The value is calculated, recalculated on every save and live in the form. Cannot be edited. |
| | **Default** starting with `=` | An expression worked out once, when a document is created. `=today()` |
| | **Fetch from** (`fetchFrom`, `fetchIfEmpty`) | `link_field.target_field`: copy a value when the link changes. Not an expression. |
| DocType → Rules | **The rule holds when** | The document (or each row of a child table) is refused unless this is true. The message is shown under the chosen field. |

## Values

`null` (no value), `true`/`false`, numbers, text (`'single'` or `"double"` quotes, with `\\ \' \" \n \t \r` escapes), lists (`[1, 2, 3]`) and, for table fields, lists of rows. An empty input is `null`. Numbers are always 64-bit floating point, in the Desk and on the server, so results match exactly.

A name such as `qty` reads that field of the current document (or row). In a **child table**, `parent.rate` reads a field of the document the row belongs to. A table field such as `items` is a list of its rows.

## Operators (lowest precedence first)

`a ? b : c` · `??` (right side if the left is empty) · `||` · `&&` · `==` `!=` · `<` `<=` `>` `>=` `in` · `+` `-` · `*` `/` `%` · `!` and unary `-` · `.` (member access)

* `+` adds numbers, or joins text if either side is text. A missing number gives no value.
* `==` is strict: `1 == '1'` is false, `null == 0` is false. Lists never compare equal.
* `<`, `>` compare two numbers or two texts (by character code); anything else is false. Dates are ISO text, so `due < today()` works.
* `&&`, `||` and `!` treat `null`, `false`, `0`, `''` and `[]` as false, and always give `true` or `false`.
* Dividing or taking a remainder by zero gives no value.
* `x in [1, 2, 3]` is true when x equals one of the items.

Arithmetic never fails: wrong kinds of value give no value, so a rule can be checked against a half-filled form.

## Functions

| Function | Result |
|---|---|
| `empty(x)` | true for no value, `''` or `[]` |
| `len(x)` | length of text or a list |
| `lower(t)` `upper(t)` `trim(t)` | ASCII case change / trimmed spaces |
| `contains(t, part)` `startsWith(t, p)` `endsWith(t, p)` | text tests (case-sensitive); `contains(list, x)` too |
| `round(n, digits)` | half away from zero, digits 0 to 9 |
| `abs(n)` `floor(n)` `ceil(n)` `min(a, b, …)` `max(a, b, …)` | numbers |
| `sum(table, 'field')` | adds a field over the rows (`sum(list)` adds numbers) |
| `count(table)` | number of rows |
| `today()` `now()` | current date (`yyyy-MM-dd`) and instant, in UTC |
| `user()` `hasRole('Role')` `isNew()` | the person and whether the document is new |
| `text(x)` `number(t)` | conversions (`number('42')` is 42, `number('abc')` is no value) |
| `addDays(date, n)` `daysBetween(a, b)` | date arithmetic on `yyyy-MM-dd` text |

Unknown functions, wrong argument counts, unknown field names and circular formulas are rejected when the DocType is saved, with the position of the problem. An expression is at most 1000 characters.

## Examples

```
status == 'Closed'                         Show only when
amount > 1000                              Required when
qty * rate                                 Calculate as (in a table row)
sum(items, 'amount')                       Calculate as (on the document)
round(total * (1 - discount / 100), 2)     Calculate as
addDays(order_date, 30)                    Calculate as (a due date)
=today()                                   Default
=user()                                    Default
end_date >= start_date                     Rule: "The end date cannot be before the start date"
count(items) > 0                           Rule: "Add at least one line"
empty(qty) || qty > 0                      Rule on a child table: "Quantity must be positive"
```

## Order of evaluation when saving

1. Values are converted to their types and ordinary defaults are applied.
2. Expression defaults (new documents only).
3. "Fetch from" values (only if the link is new or changed, and only if the person may read the linked document).
4. Formulas, rows first, then the document, repeated up to three times so values that depend on each other settle.
5. Required checks (static, or by condition, skipping fields that are not shown), type, option and link checks.
6. Validation rules, only if nothing more basic is wrong.
7. Everything is written in one transaction.
