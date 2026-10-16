# The Desk: lists, forms, searching and choosing

## The form at a glance

The bar at the top stays in view while you scroll. It shows the title, the status, and icons for the things you can do: follow (bell), assign, share, rename, delete, submit and cancel. Hover an icon to see what it does. **Save** is the only button with a word on it; **Ctrl+S** does the same. Anything that cannot be undone asks first in a small window. Long forms are split into titled sections (click a title to fold it) and tabs. If saving fails, the tab with the problem is opened and marked with a red dot. A short message at the bottom corner confirms what happened ("Saved"). On the right (below the form on a phone) you see who the document is assigned to, who it is shared with, and its details.

## Signing in

Open the site and sign in with your email and password. After several wrong attempts the account is locked for a while. Change your password from your name at the top right (Account).

## The sidebar

DocTypes you may use are grouped by module. **Tools** (System Managers only) holds the DocType Designer, Customize Form, Role Permissions and Naming Series.

## Lists

- **Search** box: finds documents by their title.
- **Filters**: add one or more conditions on any field (equals, contains, greater than, is set ...). Filters are kept in the address, so you can bookmark or share a filtered list.
- **Columns** lets you choose and order the columns, rows per page and sort for yourself; **Save filters…** keeps a set of filters; tick rows for bulk actions: see [Your lists](08-list-views-filters-bulk.md).
- Click a column heading to **sort**; use the page controls at the bottom.
- Link columns (a customer on an order) show the **name**, not the ID.
- Buttons at the top depend on your rights: **+ New**, **Export** (downloads the filtered list as CSV), **Report** (a count or total per value of a field), **Tree view** (for tree DocTypes).

## Forms

- Required fields are marked `*`. Errors appear under the field; fix them and save again.
- Some fields appear, become required or lock depending on other fields, and calculated fields fill themselves. That is by design.
- **Tables** (lines) have **Add row**, and rows can be moved and removed.
- If someone else changed the document while you edited it you are told, and can reload the latest version.
- Leaving a form with unsaved changes asks for confirmation.
- Buttons in the top right: **Share**, **Rename** (where allowed), **Submit / Cancel document** (where it applies), **Delete**, **Save**.

- **Follow** (top right) tells you about changes and comments; **Assigned to** under the form hands the document to someone; the **bell** in the top bar lists your notifications: see [Assigning, following and notifications](07-assign-follow-notifications.md).
- Under the form, **Activity** shows the document's history and comments: see [History and comments](06-history-and-comments.md).

## Choosing a linked document

Fields that point at another document (customer, territory, a person) are **search boxes**. Click, then type part of a name or ID. Choices show the name with the ID in small text beneath. Use the mouse or the arrow keys and Enter; Escape closes the list; the **✕** clears the field. You can only choose documents you are allowed to see.

## Sharing a document

Click **Share**, search for a person by name or email, choose **Can edit** if they should change it, and click **Share**. They can then open this one document even if their role would not allow it. Untick/click **Stop sharing** to take it back. Only people who may edit a document can share it.

## Mobile and right-to-left

The Desk works on phones and is ready for right-to-left languages.

## Duplicating a document
Open a document and press **Duplicate** (the two squares) to start a new one from it. The new form is filled in with the same values, except fields that must be unique, passwords and the system fields; table rows are copied too. Change what you need and press **Save**: nothing exists until then.
