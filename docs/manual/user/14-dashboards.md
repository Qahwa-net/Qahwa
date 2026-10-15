# Dashboards: numbers, charts and lists

A **dashboard** shows what matters at a glance: how many open orders there are, sales per month, the latest tickets. Open **Dashboards** under *My work* in the sidebar.

## Looking at a dashboard

Each box is a **tile**: a big **number**, a **chart** (bars, a line or a pie) or a short **list** of documents. Hover a bar, point or slice to see its value. Click the refresh icon at the top to update the numbers. A tile that says "You do not have access to this" shows data you are not allowed to see; the other tiles still work. Everyone sees only what their own permissions allow, even on a shared dashboard.

## Building one

1. Click **New dashboard**, give it a title, and tick **Share with everyone** if others should be able to open it.
2. Click **Add a tile** and choose its **Type**:
   - **Number**: pick a **DocType**, then *Count*, or *Sum*, *Average*, *Smallest* or *Largest* **of** a number field.
   - **Chart**: the same, plus **Group by** a field. A date field can be grouped **per** day, month or year (shown in date order). Choose bars, a line or a pie.
   - **List of documents**: tick the fields to show, how to order them and how many rows.
3. Under **Only documents where** add conditions, for example *status = Open* or *amount > 1000*.
4. Click **Preview** to see the tile, and **Save**.

You need the **report** permission on a DocType for numbers and charts about it (ask your administrator if tiles say you have no access). Only you can change or delete your dashboards; System Managers can change any.

## Tips

- Give tiles a short title; without one the tile is named for what it shows ("Sum of amount by region").
- Charts show the 30 biggest groups, or the latest 30 days, months or years.
- Numbers are worked out over up to 10,000 documents; a note says when a tile shows part of the data.
