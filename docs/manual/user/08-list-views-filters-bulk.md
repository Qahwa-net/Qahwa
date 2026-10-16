# Your lists: columns, saved filters and bulk actions

## Choose what you see (Columns)

On any list click **Columns**.

- **Columns shown**: add a column from the drop-down, move it with ↑ ↓, remove it with ✕. The name column and *Last Modified* are always there.
- **Rows per page**: 10, 20, 50 or 100.
- **Sort by** and the direction: how the list is ordered when you open it (you can still click any heading).
- **Save** keeps this for you only, for this DocType. **Back to the usual view** removes your settings.

You can only choose columns you are allowed to see; protected fields do not appear in the list.

## Conditions you can use
Besides *equals*, *not equals*, *greater/less*, *contains* and *is set / not set*, a filter can be:
- **does not contain** (text) and **not equals / not in**: documents with an empty value are included, because an empty value is not equal to anything;
- **between** (numbers, dates, moments): both ends are included. For a moment, a date as the end means the whole of that day;
- **in the period** (dates, moments): today, yesterday, tomorrow, this/last/next week (Monday to Sunday), month, quarter or year, the last 7, 30 or 90 days, the next 7 or 30 days.

In saved filters and in the address you can write `__user` (you), `__today` and `__now`, so a filter such as "Assigned to = __user" means *you* for everyone who uses it.

## Saved filters

1. Set filters on the list (field, condition, value).
2. Click **Save filters…**, give them a name, and **Save**. If you may edit these documents you can tick **Share with everyone**.
3. Next time pick the name from **Saved filters…**. Shared filters show who made them.

Saving under the same name replaces the old one. **Delete** (next to the picker) removes a filter you saved. You can keep up to 50 per DocType.

## Group by: counts at a glance

Click **Group by**, choose a field (a choice, a link, a yes/no field or *Created By*), and the side panel shows how many documents have each value, for the documents your current filters show. **Click a value** to filter the list by it; click it again to remove that filter. Your choice of field is remembered in this browser.

## Status colours and comment counts

If the DocType has a **status field** (set in the DocType Designer by choosing the field and a colour for each choice), each row shows it as a coloured label: for example *Open* in orange, *Done* in green. Submittable documents also show **Draft**, **Submitted** or **Cancelled**. A **💬 3** beside a name means the document has 3 comments.

## Select several documents (bulk actions)

Tick the boxes at the left of the rows (the top box selects the whole page). A bar appears:

- **Set a field…**: choose a field, type or pick the new value, **Apply**, confirm. Every selected document is changed.
- **Rename…** (only for documents that may be renamed): type the text to find and the text to put in its place, check the example shown, then **Rename**. Links to the documents are updated. Documents whose name does not contain the text are left alone; a name that is already taken is reported for that document.
- **Delete**: removes every selected document after you confirm.
- **Clear selection**.

Up to 100 documents at a time. Each document is checked on its own, exactly as if you had opened it: if you may not change one, it is locked (submitted), or the value is not allowed, that document is skipped and you are told why, and the rest are done. Changes are recorded in each document's history. Calculated fields cannot be set this way.

Changing the page, the filters or the sort clears the selection.

## Exporting
**Export CSV** and **Export Excel** (above the list) download the documents your filters show, if you have the Export right. The Excel file keeps numbers, dates and yes/no values as such. Reports have **Download CSV** and **Download Excel** too.
