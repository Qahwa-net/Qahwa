// Client Script   DocType: Pm Task
qahwa.ui.form.on('Pm Task', {
  refresh(frm) {
    var d = frm.doc;
    var today = new Date().toISOString().slice(0, 10);
    if (d.status !== 'Done' && d.due_date && d.due_date < today) frm.set_intro('This task is overdue (due ' + d.due_date + ').');
    frm.toggle_reqd('assigned_to', d.status === 'In Progress');
    if (d.status !== 'Done') {
      frm.add_custom_button('Mark as done', function (f) { f.set_value('status', 'Done'); });
    }
  },
  status(frm) {
    // Work in progress must have a person.
    frm.toggle_reqd('assigned_to', frm.doc.status === 'In Progress');
  },
  validate(frm) {
    if (frm.doc.status === 'In Progress' && !frm.doc.assigned_to) frm.throw('Choose who is working on this task.');
  },
});
