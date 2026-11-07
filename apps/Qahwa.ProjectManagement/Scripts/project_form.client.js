// Client Script   DocType: Pm Project
// Helps on the project form. It runs in the browser and can only change what the form shows.
qahwa.ui.form.on('Pm Project', {
  refresh(frm) {
    var d = frm.doc;
    if (d.budget_hours && d.logged_hours > d.budget_hours) {
      frm.set_intro('Over budget: ' + d.logged_hours + ' of ' + d.budget_hours + ' hours used.');
    } else if (d.percent_complete != null && d.percent_complete >= 0) {
      frm.set_intro(d.percent_complete + '% complete' + (d.budget_hours ? ' · ' + (d.logged_hours || 0) + ' of ' + d.budget_hours + ' hours used.' : '.'));
    }
  },
  status(frm) {
    // Say why the end date matters once the project is active.
    frm.toggle_reqd('end_date', frm.doc.status === 'Active');
  },
  validate(frm) {
    if (frm.doc.status === 'Completed' && frm.doc.percent_complete != null && frm.doc.percent_complete < 100) {
      frm.throw('Finish the open tasks before completing the project (' + frm.doc.percent_complete + '% done).');
    }
  },
});
