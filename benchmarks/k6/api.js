// Load test of a running Qahwa server: read, filter, page deep and write documents.
//
// Use an API key (Desk, Account, API keys): it needs no sign-in, so the test measures the API and not the login throttle.
//   k6 run -e BASE_URL=http://localhost:5101 -e API_KEY=... benchmarks/k6/api.js
//   docker run --rm -i -e BASE_URL=http://host.docker.internal:5101 -e API_KEY=... grafana/k6 run - < benchmarks/k6/api.js
// Or sign in once per virtual user (the server throttles repeated sign-ins, so keep VUS small):
//   k6 run -e EMAIL=... -e PASSWORD=... benchmarks/k6/api.js
//
// It uses the Note DocType (everyone has it, and each person sees their own rows). For the "list with a million rows" case seed
// them first with benchmarks/k6/seed-notes.sql, and remove them afterwards with benchmarks/k6/clean-notes.sql.
import http from 'k6/http'
import { check, group, sleep } from 'k6'

const BASE = __ENV.BASE_URL || 'http://localhost:5101'
const VUS = Number(__ENV.VUS || 10)
const DURATION = __ENV.DURATION || '30s'

export const options = {
  scenarios: {
    readers: { executor: 'constant-vus', exec: 'read', vus: VUS, duration: DURATION },
    writers: { executor: 'constant-vus', exec: 'write', vus: Math.max(1, Math.floor(VUS / 5)), duration: DURATION },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    'http_req_duration{kind:list}': ['p(95)<500'],
    // A leading-wildcard LIKE cannot use an index, so on a million rows it is a scan: this is the slow case, kept as a worst case.
    'http_req_duration{kind:filter}': ['p(95)<1500'],
    'http_req_duration{kind:deep}': ['p(95)<1500'],
    'http_req_duration{kind:get}': ['p(95)<300'],
    'http_req_duration{kind:insert}': ['p(95)<800'],
  },
}

const KEY = __ENV.API_KEY
const json = { headers: { 'Content-Type': 'application/json', ...(KEY ? { Authorization: `Bearer ${KEY}` } : {}) } }
const auth = KEY ? { headers: { Authorization: `Bearer ${KEY}` } } : {}
const q = (params) => Object.entries(params).map(([k, v]) => `${k}=${encodeURIComponent(v)}`).join('&')

function signIn() {
  const res = http.post(`${BASE}/api/auth/login`, JSON.stringify({ email: __ENV.EMAIL, password: __ENV.PASSWORD }), json)
  check(res, { 'signed in': (r) => r.status === 200 })
}

export function setup() {
  if (!KEY) signIn()
  return {}
}

// Each VU signs in once; k6 keeps its cookies for the rest of the run.
let signedIn = false
function ensureSignedIn() {
  if (!signedIn && !KEY) {
    signIn()
    signedIn = true
  }
}

const listParams = (extra) => q({ fields: JSON.stringify(['name', 'title', 'modified']), order_by: 'modified desc', limit_page_length: '20', ...extra })

export function read() {
  ensureSignedIn()
  let first
  group('list', () => {
    const res = http.get(`${BASE}/api/resource/Note?${listParams({ limit_start: '0' })}`, { ...auth, tags: { kind: 'list' } })
    check(res, { 'list ok': (r) => r.status === 200 })
    first = (res.json('data') || [])[0]
  })
  group('filter', () => {
    const filters = JSON.stringify([['title', 'like', '%7%']])
    const res = http.get(`${BASE}/api/resource/Note?${listParams({ filters, limit_start: '0' })}`, { ...auth, tags: { kind: 'filter' } })
    check(res, { 'filter ok': (r) => r.status === 200 })
  })
  group('deep page', () => {
    const res = http.get(`${BASE}/api/resource/Note?${listParams({ limit_start: '5000' })}`, { ...auth, tags: { kind: 'deep' } })
    check(res, { 'deep ok': (r) => r.status === 200 })
  })
  if (first) {
    group('get', () => {
      const res = http.get(`${BASE}/api/resource/Note/${encodeURIComponent(first.name)}`, { ...auth, tags: { kind: 'get' } })
      check(res, { 'get ok': (r) => r.status === 200 })
    })
  }
  sleep(0.2)
}

export function write() {
  ensureSignedIn()
  const res = http.post(`${BASE}/api/resource/Note`, JSON.stringify({ title: `k6 note ${__VU}-${__ITER}`, content: '<p>load test</p>' }), { ...json, tags: { kind: 'insert' } })
  check(res, { 'insert ok': (r) => r.status === 201 || r.status === 200 })
  sleep(0.5)
}
