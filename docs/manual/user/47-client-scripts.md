# Client Scripts

A **Client Script** is a few lines of JavaScript that run in the browser when someone opens a form of a DocType. Use it for small helpers on the form: a hint, hiding or requiring a field, filling one field from another, an extra button, or stopping a save with a message. Only a **System Manager** can make them. For rules that must always hold, use a **Server Script** (Validate) instead: a client script can be bypassed by someone with browser tools, the server cannot.

Open **Client Script** and create one with the **DocType** and the **Script**. The script is checked when saved. Example:

```js
qahwa.ui.form.on('Order', {
  refresh(frm) {
    frm.set_intro('Prices include VAT.');
    frm.add_custom_button('Use standard terms', function (frm) {
      frm.set_value('terms', 'Net 30');
    });
  },
  customer_type(frm) {                          // runs when the field "customer_type" changes
    frm.toggle_display('vat_number', frm.doc.customer_type === 'Company');
    frm.toggle_reqd('vat_number', frm.doc.customer_type === 'Company');
  },
  validate(frm) {                               // runs before saving
    if (frm.doc.total > 100000 && !frm.doc.approved_by) frm.throw('Orders over 100,000 need an approver.');
  },
});
```

## Events
| name | when |
|---|---|
| `onload` | once, when the form opens |
| `refresh` | after `onload` and after every save |
| `validate` | before saving (also Ctrl+S); `frm.throw("text")` stops the save and shows the text |
| a field name | when the person changes that field (not for values a script sets) |

## What `frm` offers
`frm.doc` (a copy of the fields; numbers are numbers) · `frm.set_value(field, value)` · `frm.toggle_display(field, show)` · `frm.toggle_reqd(field, required)` · `frm.toggle_enable(field, enabled)` · `frm.set_df_property(field, 'hidden' | 'read_only' | 'reqd', true/false)` · `frm.set_intro(text)` · `frm.add_custom_button(label, function (frm) {…})` · `frm.msgprint(text)` · `frm.throw(text)`.

## Good to know
- The script runs in a protected area: it cannot reach the page, other sites, your session or the server. It can only ask the form for the things above.
- A script that takes more than a second is stopped; the form shows a note and client scripts stay off for that form until it is reopened.
- Passwords, files and tables are not visible to scripts.
- At most ten active scripts per DocType, 20,000 characters each.
