# Employee Operations
Installed through the running Qahwa REST API. Open Home and select Employee Dashboard.

- Supplier: Draft -> Pending Approval -> Active; suspend and reactivate.
- Product: Draft -> Pending Approval -> Active; discontinue and reactivate.
- Purchase Order: Draft -> Pending Approval -> Approved -> Ordered -> Received; revise pending orders or cancel approved/ordered orders.
- Purchase Order Item: linked product, quantity, fetched price, calculated amount.
- Dashboard: active supplier/product counts, pending approvals, order status chart, recent orders.

Employee may create and request approval. Operations Manager and System Manager approve and manage fulfillment. Self approval is enabled for this initial setup. Assign these roles to staff through User documents.

The existing Supplier and Product fields are retained. Existing sales Order is preserved. Purchase Order uses the framework submit/cancel lifecycle. Existing master documents have no workflow state until saved; new documents start in Draft. Currency defaults to SAR and tax to 15%, both editable per order; dashboard does not sum different currencies.

To reinstall on this site, run Install.ps1 with QAHWA_API_TOKEN set to a manager token. No token is stored in this directory. The installer expects the existing Supplier and Product DocTypes and adds the supplied fields. bundle.json contains definitions; dashboard.json and workspace.json contain page requests.

## MDD reference implementation

Reference source: `D:\MDD\Git\MDD EcoSystem\employee-dashboard\src\app\modules`.

Run `node samples/employee-operations/Install-Reference.cjs` with `QAHWA_API_TOKEN` set. This supersedes the simpler initial installer for this site. It extends existing DocTypes through the API and preserves their existing fields and data. `reference-bundle.json` is the resulting metadata export. `before-reference.json` stores definition snapshots, without document records or API credentials.

Implemented reference pages:

| Reference | Qahwa page |
| --- | --- |
| suppliers/suppliers-create, supplier-details | Supplier tabs: General, Contact & Address, Contacts, Bank Accounts, Documents, Categories & Credit |
| products/product-list/product-create-model | Product: bilingual names, SKU, category, brand, unit, barcode, image, pricing, approval notes |
| Supplier/product price details | Supplier Product Price: supplier/product links, currency, quantity, fixed price, expiry, margin |
| orders/order-create, order-detail | Existing Order: customer, organization, project, assigned employee, product rows, PR/PO numbers, approvals, pricing/delivery dates and documents |
| Supplier supporting documents | Supplier Document: standalone record linked to Supplier, with attachment and expiry |

Order workflow: Draft -> Submitted -> Pricing -> Quotation Submitted -> Technical Approval -> Final Approval -> Quotation Approved -> Preparing Shipment -> Delivery in Progress -> Invoicing -> Awaiting Payment -> Completed. Approvals can reject back to revision. Cancellation requires a note. Confirm Delivery requires the delivery-note flag. These are Qahwa workflow mappings of the reference concepts, rather than a copy of backend status numbers, which are supplied dynamically by the reference application's server.

Supplier and Product workflows add Reject/Resubmit to the original approval flows. Employee submits; Operations Manager/System Manager approve and manage later stages. Technical/final approver fields record the intended assignees; transitions currently authorize by manager role. Self approval remains enabled. Existing Order is not submittable, so its workflow uses status fields (all docstatus 0); Purchase Order retains actual submit/cancel semantics.

The reference's business structure is implemented using native Qahwa generated list/form pages, rather than a pixel copy of the Angular interface. SAP/ERP sync, AI similarity matching, external payment/shipping operations, automated quote/invoice creation, source-system records and credentials are not imported. Workflow actions record progress; they do not execute those external operations. Generic Qahwa comments, history, assignment and attachments remain available on forms. Supplier documents are standalone because attachment fields are unsupported in child tables.

Verification: `node samples/employee-operations/Verify-Reference.cjs` creates a temporary Order, checks totals, traverses the order workflow, checks the delivery gate, and removes the temporary Order. It requires at least one existing Customer. Dashboard tiles were checked through their data endpoint and returned no errors.
