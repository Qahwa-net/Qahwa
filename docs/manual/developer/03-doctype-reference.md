# DocType reference

A DocType is a JSON file (one per DocType, in a folder listed in `Qahwa:DocTypePaths`) or a definition made in the DocType Designer. Property names are camelCase. Unknown properties are ignored.

```json
{
  "name": "Customer",
  "module": "Selling",
  "autoname": "CUST-.#####",
  "titleField": "customer_name",
  "fields": [
    { "name": "customer_name", "type": "Data", "label": "Customer Name", "required": true, "unique": true, "inListView": true },
    { "name": "territory", "type": "Link", "options": "Territory" },
    { "name": "credit_limit", "type": "Currency", "dependsOn": "customer_type == 'Company'" }
  ],
  "permissions": [
    { "role": "Sales Manager", "read": true, "write": true, "create": true, "delete": true },
    { "role": "Sales User", "read": true, "write": true, "create": true, "ifOwner": true }
  ],
  "validations": [
    { "expression": "credit_limit >= 0", "message": "Credit limit cannot be negative", "field": "credit_limit" }
  ]
}
```

## DocType properties

| Property | Meaning |
|---|---|
| `name` | Letters, digits, space, `_`, `-`; up to 60 characters. Unique. Becomes the table `tab<name>`. |
| `module` | Groups DocTypes in the sidebar; a Module Profile can block a module. |
| `titleField` | The field that names a document in lists and links. |
| `autoname` | `hash` (default), `prompt` (user types it), `field:<fieldname>`, or a series like `CUST-.#####` (prefix, then `.` and `#` for the digits). |
| `isChildTable` | Rows live inside another DocType through a `Table` field. No list, no permissions of its own. |
| `isSubmittable` | Draft → submitted → cancelled lifecycle. Fixed at creation. |
| `isSingle` | One document named after the DocType (settings). No list, cannot be deleted. Fixed at creation. |
| `isTree` | Adds `parent_node` and `is_group`; nodes form a tree. Fixed at creation. |
| `allowRename` | Documents can be renamed or merged. |
| `statusField` | A Select, Data or Link field shown as a coloured status in lists. |
| `indicators` | `[{ value, color }]`: the colour of each value of `statusField` (gray, blue, green, orange, red, purple); unlisted values are grey. At most 50. |
| `trackChanges` | Keep the updates log (`Version` rows). **On by default**; set `false` to opt out. Child tables are never logged on their own. |
| `fields` | See below. Up to the framework limit per DocType. |
| `permissions` | See [Permissions and access](05-permissions-and-access.md). |
| `validations` | Rules over the whole document (or each row of a child table). |

Combinations that are refused: a child table cannot be submittable, single, a tree or renamable; a single cannot be a tree, submittable or renamable and has no naming rule; a tree needs its `parent_node` Link (to itself) and `is_group` Check (added for you).

## Standard columns (every table, never declared)

`name` (the ID), `creation`, `modified`, `modified_by`, `owner`, `docstatus` (0 draft, 1 submitted, 2 cancelled), `idx`. Child tables also have `parent`, `parenttype`, `parentfield`.

## Field properties

| Property | Meaning |
|---|---|
| `name` | Lower-case letters, digits, `_`; starts with a letter. Unique in the DocType. |
| `type` | One of the types below. |
| `label` | Shown in forms and lists. |
| `options` | Link target DocType, child DocType for `Table`, or newline-separated choices for `Select`. |
| `required`, `unique` | Required on save; unique index (not in child tables). |
| `indexed` | Create a (non-unique) database index, for fields that are searched or joined on. |
| `inListView` | Shown as a list column. |
| `default` | Static text, or an expression starting with `=` such as `=today()`. |
| `lowercase` | Convert text to lower case on save (emails). |
| `description` | Help text under the field. |
| `hidden`, `readOnly` | UI hints; the value is still stored and validated. |
| `dependsOn`, `mandatoryDependsOn`, `readOnlyDependsOn` | Expressions: show, require, or lock the field while true. |
| `formula` | Calculated on every save; cannot be edited. |
| `fetchFrom`, `fetchIfEmpty` | `link_field.target_field`: copy a value from the linked document. |
| `permLevel` | 0 to 9. Above 0 the field is only visible/editable to roles with a rule at that level. |
| `allowOnSubmit` | Submittable DocTypes: still editable after submit. |

Expressions are documented in [the expression language](../../expressions.md).

### Field types

| Type | Stores | Notes |
|---|---|---|
| `Data` `Email` `Phone` `Url` | short text (140) | `Email` is validated |
| `Text` `LongText` `Markdown` `Code` `Json` | long text | |
| `Int` `Float` `Decimal` `Currency` `Percent` | numbers | `Decimal`/`Currency` use exact decimals |
| `Check` | boolean | |
| `Select` | text from `options` | |
| `Date` `Datetime` `Time` | ISO values | |
| `Link` | name of a document | checked on save; shown by its title |
| `Table` | child rows | `options` is the child DocType |
| `Password` | hashed text | never listed or exported |
| `Attach` `AttachImage` `DynamicLink` | planned | |
| `SectionBreak` `ColumnBreak` `TabBreak` | layout only | no column |

## Permission rule properties

`role`, `read`, `write`, `create`, `delete`, `export`, `report`, `submit`, `cancel`, `ifOwner`, `permLevel`. `create`, `delete`, `export`, `report`, `submit` and `cancel` only apply at level 0. `submit` and `cancel` only on submittable DocTypes.

## Validation rule properties

`expression` (must be true), `message`, `field` (optional: show the message under this field).

## Core DocTypes

`User` (with `role_profile`, `module_profile`), `Role`, `Has Role`, `Role Profile` (+ child `Role Profile Role`), `Module Profile` (+ child `Block Module`), `DocShare`, `User Permission`, `Version` (the updates log, read-only even for System Managers), `Comment`, `ToDo` (assignments), `Document Follow`, `Notification Log`, `Saved Filter` and `List Setting`. The last seven are written by services, never through the generic API. They live in `src/Qahwa.Core/CoreDocTypes` as embedded JSON.

## Tables the framework creates besides DocType tables

`tabSeries` (naming counters), `__auth` (password hashes, lockout), `__doctype` (designer DocTypes), `__permissions` (permission overrides), `__customize` (Customize Form), `__workflow` (Workflow Builder), `__assignment_rule` (Assignment Rules). The Core DocType `Auto Repeat` holds repeats (written only by `AutoRepeatService`). `ToDo` has an `assignment_rule` column naming the rule that made an assignment.
