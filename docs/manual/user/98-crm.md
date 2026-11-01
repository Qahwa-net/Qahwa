# CRM: leads and opportunities

**Qahwa CRM** is a small sales tracker that comes with Qahwa. It has two DocTypes in the **CRM** module: **Crm Lead** (a possible customer) and **Crm Opportunity** (a sale you are working on). Everything else a sales team needs is already in Qahwa: assign a lead to a colleague, comment and @mention, set a **Remind me** on a lead, put a call in your calendar as an **Event**, add tags, send an email, print.

## Turn it on
CRM is off until the site's settings turn it on. An administrator sets `Qahwa:Apps:Crm` to `true` for the site and restarts the server. It then appears as an installed app, creates the two roles below, and adds the CRM module to the sidebar. Give people the roles in their User documents.

| Role | What it can do |
|---|---|
| **CRM User** | Create leads and opportunities and work on **their own** (and see only their own). |
| **CRM Manager** | See and change everyone's, delete, export and run reports. |

## Leads
A lead has a name, company, email, phone, source and a **status**: New, Contacted, Qualified, Converted or Lost. Leads open as a **board** with a column per status; drag a card to change its status, or open it and pick one. A lost lead says **why** (the box appears when you choose Lost and cannot be left empty).

## Convert a lead
When a lead is ready, open it and use **Convert to opportunity** (the app's `qahwa_crm.convert_lead` method; an administrator can add a button for it with a Document Action). It makes an opportunity titled after the company, with the lead's company filled in, marks the lead **Converted** and links the two. A lead can only become Converted this way, a converted lead cannot be reopened (work on its opportunity), and a lost lead cannot be converted.

## Opportunities
An opportunity has a title, the lead it came from, an **amount**, an **expected close** date, a next step and a **stage**: Prospecting, Proposal, Negotiation, Won or Lost.

- Each stage has a typical **probability**: 10%, 40%, 70%, 100% and 0%. Moving an opportunity to a stage sets it, unless you set a probability yourself in the same save; Won and Lost always set 100% and 0%.
- **Weighted amount** is the amount times the probability: what the opportunity is worth today.
- A lost opportunity says why.

## The pipeline report
Managers and users can open **CRM pipeline** (Reports, Script reports): for each stage, the number of opportunities, their total amount and their weighted amount. You only see what you may read, so a salesperson's pipeline is their own and a manager's is everyone's.
