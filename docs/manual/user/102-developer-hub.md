# Developer Hub

The **Developer Hub** (sidebar, *Tools*, for System Managers) is one page that lists every tool for building and running your system, grouped by the job: *Model your data*, *Control access*, *Automate*, *Connect other systems*, *Look and feel* and *Run and look after*. Each line says what the tool is for.

Type in the box at the top to find a tool: it looks at the name, the description and related words, so `cron` finds *Scheduled Jobs* and `signed` finds *Webhooks*. Click a tool to open it.

## Calling your system from a program

Open a DocType in the **DocType Designer** and choose the **API** tab. It shows ready-made calls for that DocType (list, read one, create, update, delete, and submit when it can be submitted) using the DocType's own required fields in the example. Each has a **Copy** button.

1. In **My account**, open **API keys** and make a key. Copy it when it is shown: it is never shown again.
2. Put it in your shell: `export QAHWA_KEY=qk_...`
3. Paste a call. The program acts as you, with your permissions. A *read only* key can only list and read.

The link at the top of the tab opens `/api/openapi.json`, the complete list of calls for Postman or a code generator.
