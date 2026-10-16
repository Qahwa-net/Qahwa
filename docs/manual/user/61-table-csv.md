# Tables: download and upload as CSV

Under a table in a form (the lines of an order, for example):

- **Download CSV** saves the rows as a file you can open in Excel. The first line holds the column names.
- **Upload CSV** adds the rows of a CSV file to the end of the table. Use a file made by **Download CSV** (you can add or change lines in Excel and save as CSV), or any file whose first line has the names of the table's columns. Columns can be in any order; columns that do not match are ignored and listed.

Notes:
- Uploaded rows are added, never replaced. Delete rows first if you want to start again.
- Up to 1,000 rows and 1 MB. Check boxes are read from Yes/No (or true/false, 1/0).
- Nothing is saved until you save the form; then each value is checked, and any problem is shown on its row.
- Text that begins with `=`, `+`, `-` or `@` gets a small quote in front when downloaded, so Excel treats it as text; it is taken away again when you upload.
