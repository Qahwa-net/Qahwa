# Scripts for the list view

A Client Script (Tools or the **Client Script** list; System Managers) can also change how a list looks. In the script:

```js
qahwa.ui.list.on('Order', {
  onload(list) {
    list.set_intro('Overdue orders are marked red.')
    list.add_custom_button('Show open', (l) => l.set_filter('status', 'Open'))
  },
  refresh(list) {
    list.rows.forEach((r) => { if (r.total > 1000) list.set_row_indicator(r.name, 'red') })
  },
})
```

- `list.rows`: the rows on the page. `list.selected`: the names ticked.
- `list.add_custom_button(label, handler)`: a button above the list; the handler gets the same `list`.
- `list.set_intro(text)`, `list.msgprint(text)`, `list.set_row_indicator(name, color)`, `list.set_filter(field, value)`.
- Filters set from `refresh` are ignored (to stop endless reloading); set them from `onload` or a button.

Scripts run in a sandbox with no access to the network or storage, and can only ask the page to do the things above.
