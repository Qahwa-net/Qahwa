# Special documents

## Settings pages (single DocTypes)

A **single** DocType has exactly one document, such as *Site Settings*. Opening it in the sidebar goes straight to its form (no list). Until someone saves it, it shows the default values; the first save creates it. It cannot be deleted.

## Trees

A **tree** DocType (for example regions or departments) has **Parent** and **Is Group** fields. Only groups can have children. Use **Tree view** on the list to browse: click ▸ to open a group. You cannot make a node its own parent or child, delete a node that has children, or turn a group with children into a plain node.

## Submit and cancel

On a **submittable** DocType, new documents are **Drafts**. **Submit** (needs the Submit right, and the form must be saved) locks the document: only fields marked "can still be changed after submit" stay editable, and it cannot be deleted. **Cancel document** (needs the Cancel right) locks it for good; a cancelled document can then be deleted. The badge at the top shows Draft, Submitted or Cancelled.

## Rename and merge

Where a DocType allows it, **Rename** changes a document's name; every link to it, its table rows, shares and user permissions follow. Tick **Merge into an existing ...** to combine two documents: the old one is removed and everything that used it now uses the other (you are asked to confirm, and need the right to delete). A submitted document must be cancelled first.

## Export and reports

If you have the rights, **Export** on a list downloads the current (filtered) list as a CSV file for a spreadsheet (up to 10,000 rows; passwords and fields you cannot see are never included; cells that could run as spreadsheet formulas are made safe). **Report** shows a count, or a sum/average/smallest/largest of a number field, for each value of another field; it uses the list's filters.

## Naming series (Tools → Naming Series)

Shows each numbered series (for example *CUST-00041*), how many digits it has, which DocTypes use it and the last number used. Change the number to move a series (the next document gets the number after it). Setting it lower than numbers already in use makes new documents clash with old ones.
