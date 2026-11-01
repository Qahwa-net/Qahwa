# ADR-146: Qahwa CRM-lite, the first flagship app

**Status:** accepted (apps)

## Context
The plan calls for a first flagship app to dogfood the framework, and chose the smallest useful surface: a CRM-lite. An app is the way a business module ships (ADR-062), so building one proves what the app surface can and cannot do.

## Decision
- `apps/Qahwa.Crm` is a class library (`CrmApp : IQahwaApp`, name `qahwa_crm`) with its DocTypes as JSON files beside the assembly, tested by `apps/Qahwa.Crm.Tests` through the same `TestEngine` every framework test uses.
- **Two DocTypes in the module CRM.** `Crm Lead` (name, company, email, phone, source, status New, Contacted, Qualified, Converted, Lost, a reason when lost, notes, the opportunity it became) and `Crm Opportunity` (title, lead, company fetched from it, stage Prospecting, Proposal, Negotiation, Won, Lost, amount, probability, weighted amount, expected close, next step, reason when lost). Both open as a **board** by status and carry the status colours. Reminders, events, comments, assignments, tags, email and print come from the framework, not from the app.
- **Rules are kept where they belong.** Mandatory-when-lost, the 0 to 100 probability, the fetched company and the default status are DocType metadata. Two before-save hooks hold what metadata cannot say: a stage sets its typical probability (10, 40, 70, 100, 0) unless the person set one in the same save, and the weighted amount is worked out in the hook too (hooks run after the formulas); a lead becomes Converted only through conversion and a converted lead cannot be reopened.
- **Lead conversion is a server function**, `POST /api/method/qahwa_crm.convert_lead`, that creates the opportunity and updates the lead **as the person who called it**, so every permission and rule applies (a person who may not create opportunities cannot convert). It refuses a converted or lost lead.
- **Roles and visibility.** `CRM User` works on their own leads and opportunities (if-owner rules: they see only their own) and `CRM Manager` sees, changes, deletes and reports on everyone's. The app creates both roles as fixtures. A script report, `qahwa_crm.pipeline`, adds up each stage (count, amount, weighted amount) from the rows the caller may read.
- **Off until a site asks.** The reference server references the app but registers it only for a site whose own settings say `Qahwa:Apps:Crm = true`, so no existing site gains DocTypes or roles by upgrading.
- **Verified for real.** Eleven unit tests cover the rules and permissions; a run of the server against PostgreSQL with the app on made the tables, created a lead, converted it, moved the opportunity (70% and a weighted amount of 3,500 on 5,000), refused a second conversion and ran the pipeline report. The test data, tables and roles were then removed from that database.

## Consequences
- The app proved the surface is enough for a small module and exposed one thing to know: hooks run after formulas, so a hook that changes a field a formula uses must update the formula's result itself.
- No button for conversion ships yet: it is a method an administrator can attach to a form with a Document Action, or call from a client script.
- Not yet: contacts and companies as their own DocTypes (the framework's Contact and Address exist and can be linked), quotations, email-to-lead, a dashboard and a workspace for CRM, and Arabic labels.
