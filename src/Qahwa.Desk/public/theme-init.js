// Applies the person's saved theme before the page paints, so a chosen light or dark theme does not flash the other one.
try {
  var t = localStorage.getItem('qahwa.theme')
  if (t === 'light' || t === 'dark') document.documentElement.setAttribute('data-theme', t)
} catch (e) { /* storage unavailable: the system setting applies */ }
