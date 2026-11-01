# Prepared reports: big reports built in the background

Opening a report runs it now and shows the first rows (up to 1,000). A **prepared report** is for the big ones: the server builds it in the background, with all its rows, and keeps the result for a week, so you can leave and come back.

## Prepare one
1. Open a report (Reports, then its name).
2. At the bottom, under **Prepared versions**, click **Prepare in the background**.
3. It appears as **Waiting**, then **Building…**, then the number of rows. The list refreshes by itself.
4. Click **Open** to read it, or **Download CSV** inside to get every row.

## Good to know
- **It is built as you.** You see the rows you may see, exactly as when you download the report. A list report needs the **export** right on its DocType (as its download does); without it the prepared version fails with a message saying so.
- You can have 3 waiting or building at once. Asking again for a report that is already waiting gives you that one.
- A prepared report is kept for **7 days**, then deleted; you can delete it sooner with the bin.
- If the server was restarted while it was building, it is marked **Failed** and you can prepare it again.
- A result too large to keep (about 8 MB) fails with a message; add a filter or fewer columns.
- The preview in the Desk shows the first 500 rows; the CSV has all of them.
- Nobody else can open your prepared reports (a System Manager can read them in the Prepared Report list).
