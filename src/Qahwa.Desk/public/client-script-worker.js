// The sandbox for Client Scripts. It runs in a Web Worker: no page, no DOM, no cookies. Before any script runs, everything that could
// reach the network, storage or other workers is removed, and the only way out is the list of operations returned to the page, which
// the page checks again before doing anything (see lib/clientScripts.ts). A script that loops forever only hangs this worker; the page
// stops waiting after a second and ends it.
'use strict'

;(function () {
  var post = self.postMessage.bind(self)
  var listen = self.addEventListener.bind(self)

  var BLOCKED = [
    'fetch', 'XMLHttpRequest', 'WebSocket', 'EventSource', 'importScripts', 'indexedDB', 'caches', 'Worker', 'SharedWorker',
    'BroadcastChannel', 'WebTransport', 'Request', 'Response', 'Headers', 'FileReader', 'FileReaderSync', 'URL', 'Blob',
    'WebAssembly', 'localStorage', 'sessionStorage', 'postMessage', 'XMLHttpRequestUpload', 'navigator', 'location',
  ]
  BLOCKED.forEach(function (name) {
    try {
      Object.defineProperty(self, name, { value: undefined, writable: false, configurable: false })
    } catch (e) {
      try { self[name] = undefined } catch (e2) { /* already fixed */ }
    }
  })
  // Some of these live on the prototype chain.
  var proto = Object.getPrototypeOf(self)
  while (proto && proto !== Object.prototype) {
    BLOCKED.forEach(function (name) {
      try { Object.defineProperty(proto, name, { value: undefined, writable: false, configurable: false }) } catch (e) { /* not there */ }
    })
    proto = Object.getPrototypeOf(proto)
  }

  var MAX_OPS = 100
  var registered = [] // { doctype, handlers }
  var buttons = {} // id -> function
  var doctype = ''

  var stop = {}
  function ValidationStop() {}
  ValidationStop.prototype = stop

  function makeFrm(doc, ops) {
    var frm = {
      doctype: doctype,
      doc: JSON.parse(JSON.stringify(doc || {})),
      set_value: function (field, value) {
        if (typeof field === 'object' && field) {
          Object.keys(field).forEach(function (k) { frm.set_value(k, field[k]) })
          return
        }
        ops.push({ op: 'set_value', field: String(field), value: value })
        frm.doc[field] = value
      },
      set_df_property: function (field, property, value) {
        ops.push({ op: 'set_df_property', field: String(field), property: String(property), value: value })
      },
      toggle_display: function (field, show) { frm.set_df_property(field, 'hidden', !show) },
      toggle_reqd: function (field, required) { frm.set_df_property(field, 'reqd', Boolean(required)) },
      toggle_enable: function (field, enabled) { frm.set_df_property(field, 'read_only', !enabled) },
      set_intro: function (text) { ops.push({ op: 'set_intro', text: String(text == null ? '' : text) }) },
      add_custom_button: function (label, handler) {
        var id = 'b' + Object.keys(buttons).length + ':' + String(label)
        buttons[id] = handler
        ops.push({ op: 'button', id: id, label: String(label) })
      },
      msgprint: function (text) { ops.push({ op: 'msgprint', text: String(text == null ? '' : text) }) },
      throw: function (message) {
        ops.push({ op: 'throw', message: String(message == null ? 'The change was refused.' : message) })
        throw new ValidationStop()
      },
      log: function () { /* nothing to show */ },
    }
    return frm
  }

  function run(fn, doc) {
    var ops = []
    try {
      fn(makeFrm(doc, ops))
    } catch (e) {
      if (!(e instanceof ValidationStop)) ops.push({ op: 'error', message: String(e && e.message ? e.message : e) })
    }
    return ops.slice(0, MAX_OPS)
  }

  function handlersFor(event) {
    var found = []
    registered.forEach(function (entry) {
      if (entry.doctype !== doctype) return
      var h = entry.handlers[event]
      if (typeof h === 'function') found.push(h)
      else if (Array.isArray(h)) h.forEach(function (f) { if (typeof f === 'function') found.push(f) })
    })
    return found
  }

  var qahwa = Object.freeze({
    ui: Object.freeze({
      form: Object.freeze({
        on: function (target, handlers) {
          if (typeof target === 'string' && handlers && typeof handlers === 'object') registered.push({ doctype: target, handlers: handlers })
        },
      }),
    }),
  })

  listen('message', function (message) {
    var m = message.data || {}
    if (m.type === 'load') {
      doctype = String(m.doctype || '')
      var errors = []
      ;(m.scripts || []).forEach(function (s) {
        try {
          new Function('qahwa', '"use strict";\n' + String(s.source))(qahwa)
        } catch (e) {
          errors.push({ name: String(s.name), message: String(e && e.message ? e.message : e) })
        }
      })
      post({ type: 'loaded', errors: errors })
    } else if (m.type === 'event') {
      var ops = []
      var done = false
      handlersFor(String(m.event)).forEach(function (fn) {
        if (done) return
        var more = run(fn, m.doc)
        ops = ops.concat(more)
        if (more.some(function (o) { return o.op === 'throw' })) done = true // the first refusal ends the event
      })
      post({ type: 'result', id: m.id, ops: ops.slice(0, MAX_OPS) })
    } else if (m.type === 'button') {
      var handler = buttons[String(m.button)]
      post({ type: 'result', id: m.id, ops: typeof handler === 'function' ? run(handler, m.doc) : [] })
    }
  })
})()
