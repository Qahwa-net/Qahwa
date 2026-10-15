# Printing documents

## Printing a document

1. Open the document and click the **Print** icon (a printer) at the top.
2. Choose a **Format** and a **Letter head** (or leave them on *Default*). The preview shows how the page will look.
3. Click **Open to print**. The page opens in a new tab: click **Print**, or press Ctrl+P, and choose a printer or **Save as PDF**.

You see only the fields you are allowed to see, and you need permission to open the document to print it.

## Print formats (System Managers)

Open **Print Formats** under Tools. A format is a page layout written in HTML with placeholders:

| Write | To get |
|---|---|
| `{{customer}}` | the value of the field *customer* |
| `{{label:customer}}` | the field's label ("Customer Name") |
| `{{#if paid}}Paid{{else}}Owing{{/if}}` | text that depends on a field |
| `{{#each items}}<tr><td>{{@index}}</td><td>{{item}}</td></tr>{{/each}}` | one piece for each row of a table |
| `{{name}}`, `{{doctype}}`, `{{today}}`, `{{user}}`, `{{status}}` | the document's ID, kind, today's date, the person printing and Draft/Submitted/Cancelled |

Pick the DocType, write the format (use **Insert a field** and **Insert a table** to avoid typos), choose a document and click **Preview**, then **Save format**. Tick *Use by default* to print every document of that kind with it. Formats are plain layout: scripts, forms and event attributes (such as `onclick`) are refused, and values from documents are always shown as plain text.

If you do not make a format, a clean layout of all the fields and tables is used.

## Letter heads

A **letter head** is a header and footer printed on the page, for your logo, address and a thank-you line. Make them in the *Letter heads* tab (HTML again), and mark one as the default. A format can name its own letter head, and when printing you can choose another or *None*.

## Tips

- Embed a small logo as an image from an `https://` address, or as an embedded `data:image/png;base64,…` picture.
- Use `<style>` for fonts and colours. Printing hides the Print button automatically.
- Missing values print as nothing; use `{{#if field}}` to hide a whole line.
