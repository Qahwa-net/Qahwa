# Importing data from a spreadsheet

You can create many documents at once from a spreadsheet: customers, items, contacts. Save the spreadsheet as **CSV** (in Excel: File, Save As, *CSV UTF-8*), then import it.

## Steps

1. Open the list of the documents (for example *Customer*) and click **Import**. You need permission to create them.
2. Click **Download a template** to get a file with the right column names, fill it in, and save it as CSV.
3. Choose **Create new**, then **choose your file**. The dialog shows how each column was matched (a column that matches nothing says *ignored*), the first rows, and any problems it found.
4. Click **Import**. You see how many were created and which rows failed, with the row number in your file and the reason.

## The file

- The **first row** holds the column names: the field's name or its label (*Customer Name*), in any case.
- One row is one document. Leave a cell empty to use the field's default.
- **Yes/No fields**: yes, no, true, false, 1 or 0.
- **Dates**: 2026-03-31 (year-month-day). **Numbers**: no thousands separators (1250.50).
- **Choice fields** must use one of the choices exactly as written.
- At most **5,000 rows** and 5 MB per file.
- Fields that are calculated, passwords, files or system columns (who created it, when) cannot be filled from a file.

## Changing existing documents

Choose **Update existing** (if you may edit them). The file needs a column named **name** holding each document's ID, plus the columns to change. An **empty cell clears** the value. You can get the IDs by exporting the list first.

## Good to know

- Every row is treated as if you had typed it: the same rules, permissions, numbering, notifications and approvals apply.
- A row that fails does not stop the others. Fix those rows in your file and import just them again. Tick **Stop at the first row that fails** if you prefer to stop.
- Importing cannot be undone as a whole; check the preview first, and try a few rows before a big file.

## Excel files
You can import an **Excel file (.xlsx)** as well as a CSV file: choose it in the Import dialog. The first sheet is used, and its first row must name the columns, exactly as for CSV. Dates should be real Excel dates or text such as `2026-03-04`. The file may be up to 10 MB; the old `.xls` format is not supported (use *Save as → Excel Workbook*).
