# Export and import (bundles)

**Tools → Export & Import** (System Manager only). A **bundle** is one file holding what you built in the Desk, so you can move it from a test site to a live one.

## What is in a bundle
- DocTypes you made in the DocType Designer
- Customizations (custom fields and changes to existing DocTypes)
- Workflows
- Assignment rules and notification rules

**Not included:** documents (your data), users, webhooks (they hold secrets and addresses that belong to one site), print formats, dashboards, workspaces and files.

## Export
1. Give the bundle a name (optional) and tick what to include.
2. Click **Download bundle**.

## Import
1. Choose the bundle file. A **preview** appears at once: for each item it says whether it would be created or updated, or why it would be refused. Nothing has changed yet.
2. Click **Apply** to import. The result shows every item.

Good to know:
- Each item is checked exactly as if you had typed it in, so one wrong item is refused with a reason and the rest still go in.
- Importing the same file again updates instead of failing.
- A bundle can never replace a built-in DocType.
- Customizations and workflows need their DocType to exist. A DocType in the same bundle is created first.
