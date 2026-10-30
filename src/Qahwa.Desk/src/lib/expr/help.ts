/** Short documentation of every function, shown next to expression inputs in the designer. */
export interface FunctionHelp {
  name: string
  signature: string
  description: string
  example: string
}

export const FUNCTION_HELP: FunctionHelp[] = [
  { name: 'empty', signature: 'empty(x)', description: 'True when x has no value: nothing, empty text or an empty list.', example: "empty(email)" },
  { name: 'len', signature: 'len(x)', description: 'Length of text, or the number of items in a list.', example: "len(title) > 3" },
  { name: 'lower', signature: 'lower(text)', description: 'Lower case (A-Z only).', example: "lower(email)" },
  { name: 'upper', signature: 'upper(text)', description: 'Upper case (a-z only).', example: "upper(code)" },
  { name: 'trim', signature: 'trim(text)', description: 'Removes spaces at both ends.', example: "trim(name)" },
  { name: 'contains', signature: 'contains(text, part)', description: 'True when text contains part (case-sensitive), or a list contains a value.', example: "contains(email, '@')" },
  { name: 'startsWith', signature: 'startsWith(text, start)', description: 'True when text begins with start.', example: "startsWith(code, 'INV')" },
  { name: 'endsWith', signature: 'endsWith(text, end)', description: 'True when text ends with end.', example: "endsWith(file, '.pdf')" },
  { name: 'round', signature: 'round(number, digits)', description: 'Rounds half away from zero. digits is optional (0 to 9).', example: "round(total * 0.15, 2)" },
  { name: 'abs', signature: 'abs(number)', description: 'Absolute value.', example: "abs(balance)" },
  { name: 'floor', signature: 'floor(number)', description: 'Rounds down.', example: "floor(qty / 12)" },
  { name: 'ceil', signature: 'ceil(number)', description: 'Rounds up.', example: "ceil(qty / 12)" },
  { name: 'min', signature: 'min(a, b, ...)', description: 'The smallest number.', example: "min(qty, 10)" },
  { name: 'max', signature: 'max(a, b, ...)', description: 'The largest number.', example: "max(0, balance)" },
  { name: 'sum', signature: "sum(table, 'field')", description: "Adds up a field over the rows of a table. Without the field, adds a list of numbers.", example: "sum(items, 'amount')" },
  { name: 'count', signature: 'count(table)', description: 'Number of rows in a table.', example: "count(items) > 0" },
  { name: 'lookup', signature: "lookup(link_field, 'field')", description: "A field of the document a Link field points at (nothing when the link is empty or you may not read that document).", example: "lookup(customer, 'credit_limit') - total >= 0" },
  { name: 'avg', signature: "avg(table, 'field')", description: "The average of a field over the rows of a table (rows with no number are skipped; nothing if there are none).", example: "avg(items, 'rating') >= 3" },
  { name: 'minOf', signature: "minOf(table, 'field')", description: "The smallest value of a field over the rows of a table.", example: "minOf(items, 'qty') > 0" },
  { name: 'maxOf', signature: "maxOf(table, 'field')", description: "The largest value of a field over the rows of a table.", example: "maxOf(items, 'discount') <= 20" },
  { name: 'countIf', signature: "countIf(table, 'field', value)", description: "How many rows have this value in the field.", example: "countIf(approvers, 'role', 'Manager') >= 1" },
  { name: 'sumIf', signature: "sumIf(table, 'field', 'matchField', value)", description: "Adds a field over the rows where another field has the value.", example: "sumIf(items, 'amount', 'kind', 'Tax') < 1000" },
  { name: 'distinct', signature: "distinct(table, 'field')", description: "The different values of a field over the rows (or of a list). Use len() to count them.", example: "len(distinct(items, 'item')) == count(items)" },
  { name: 'today', signature: 'today()', description: 'Today as a date (yyyy-MM-dd, UTC).', example: "due_date >= today()" },
  { name: 'now', signature: 'now()', description: 'The current date and time (UTC).', example: "now()" },
  { name: 'user', signature: 'user()', description: 'The email of the person editing.', example: "user()" },
  { name: 'hasRole', signature: "hasRole('Role')", description: 'True when the person has the role.', example: "hasRole('Accounts')" },
  { name: 'isNew', signature: 'isNew()', description: 'True while creating a document, false when editing a saved one.', example: "isNew() ? 'Draft' : status" },
  { name: 'text', signature: 'text(x)', description: 'Turns a value into text.', example: "'INV-' + text(number)" },
  { name: 'number', signature: 'number(text)', description: 'Turns text like "42" or "-1.5" into a number (nothing otherwise).', example: "number(code) + 1" },
  { name: 'addDays', signature: 'addDays(date, days)', description: 'A date some days later (or earlier with a negative number).', example: "addDays(order_date, 30)" },
  { name: 'daysBetween', signature: 'daysBetween(from, to)', description: 'Number of days from one date to another.', example: "daysBetween(start, end) <= 14" },
]

export const OPERATOR_HELP =
  'Operators: + - * / %   == != < <= > >=   && || !   ?? (use the right side when the left is empty)   a ? b : c   x in [1, 2, 3]. ' +
  'In a table row, parent.field reads the document the row belongs to.'
