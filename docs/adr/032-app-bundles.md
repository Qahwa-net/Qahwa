# ADR-032: App bundles (export and import of what was built in the Desk)

**Status:** accepted (L12, part 2)

## Context
Everything an app needs is data made in the Desk: DocTypes, customizations, workflows and rules. To move it from a test site to a live one (or share it as an "app"), it must be exportable as a file and importable again. An import is also the easiest way to attack a system, so it must never be able to do more than a person typing the same thing in.

## Decision
- **One JSON file** with a header (`format: "qahwa-bundle"`, `version: 1`, name, date) and a list of items `{kind, doctype, definition}`. Kinds: `DocType` (only those made in the Desk), `Customization`, `Workflow`, `AssignmentRules`, `NotificationRules`. The definitions are exactly what the stores already keep, so the file is readable, diff-able and Git-friendly.
- **Import goes through the existing managers** (`DocTypeDesigner`, `CustomizationManager`, `WorkflowManager`, `AssignmentRuleManager`, `NotificationRuleManager`). Every definition is parsed and validated by the same code as when typed in the Desk (identifier whitelists, expression checks, additive-only schema changes), and permissions apply (System Manager). The bundle has no way to run code, call a URL or write a row of data.
- **Items stand alone.** Each item is applied on its own; one that is refused is reported with the reason and the others still go in. There is no all-or-nothing transaction (schema changes are not transactional), so the report is the record. Importing again updates rather than failing (it reads the current `modified` first).
- **Preview first.** `dryRun` is the default: it parses, checks the DocType exists (or comes in the same bundle) and says "create" or "update" per item, without saving. The full validation happens when applying, and each failure is shown.
- **Order.** DocTypes first, child tables before the DocTypes that use them, then customizations, workflows and rules.
- **Built-in DocTypes cannot be overwritten**: a `DocType` item whose name belongs to the framework or an app is refused.
- **Webhooks are not part of a bundle.** They hold a secret (never exported) and addresses that differ per site; importing them half-configured would be worse than setting them up on each site.
- **Limits.** 5 MB and 1000 items; the body must be `application/json` (a page on another site cannot send that without a CORS preflight); the export is `no-store`, `nosniff` and a download.
- **Not included:** documents (data), users and roles, print formats, dashboards, workspaces, email templates (they are documents, not definitions), files.

## Consequences
- Moving a setup between sites is a download and an upload, with a preview.
- Not yet: a signed or versioned app package with patches, dependencies and install/uninstall; including the document-style definitions above; a CLI.
