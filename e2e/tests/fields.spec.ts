import { expect, test, type Page } from '@playwright/test'

const email = process.env.QAHWA_E2E_EMAIL
const password = process.env.QAHWA_E2E_PASSWORD

test.skip(!email || !password, 'Set QAHWA_E2E_EMAIL and QAHWA_E2E_PASSWORD to run the browser tests.')

async function signIn(page: Page) {
  await page.goto('/')
  await expect(page.getByRole('heading', { name: /^Welcome/ })).toBeVisible()
}

const json = { 'Content-Type': 'application/json' }

test('a link offers a preview card of the fields its DocType marked, and translatable values follow the language', async ({ page }) => {
  await signIn(page)
  const stamp = Date.now().toString(36)
  const parent = `E2E Area ${stamp}`
  const child = `E2E Issue ${stamp}`
  const roles = [{ role: 'System Manager', read: true, write: true, create: true, delete: true }]

  // The two DocTypes: the area marks two fields for the preview, and its status is translatable.
  const area = await page.request.post('/api/designer/doctypes', {
    headers: json,
    data: { definition: { name: parent, module: 'E2E', autoname: 'field:title', titleField: 'title', permissions: roles, fields: [
      { name: 'title', type: 'Data', required: true, label: 'Title' },
      { name: 'manager', type: 'Data', label: 'Manager', inPreview: true },
      { name: 'status', type: 'Select', options: 'Open', label: 'Status', inPreview: true, translatable: true, inListView: true },
      { name: 'secret', type: 'Data', label: 'Secret' },
    ] } },
  })
  expect(area.status()).toBe(201)
  const issue = await page.request.post('/api/designer/doctypes', {
    headers: json,
    data: { definition: { name: child, module: 'E2E', permissions: roles, fields: [{ name: 'area', type: 'Link', options: parent, label: 'Area' }] } },
  })
  expect(issue.status()).toBe(201)
  const made = await page.request.post(`/api/resource/${encodeURIComponent(parent)}`, {
    headers: json, data: { title: 'North', manager: 'Layla', status: 'Open', secret: 'do-not-show' },
  })
  expect(made.status()).toBe(201)
  await page.request.post('/api/resource/Translation', { headers: json, data: { language: 'ar', source_text: 'Open', translated_text: 'مفتوح' } })

  // The preview card shows the marked fields and nothing else.
  await page.goto(`/app/${encodeURIComponent(child)}/new`)
  await page.getByLabel('Area').fill('North')
  await page.getByRole('option', { name: /North/ }).first().click()
  await page.getByRole('button', { name: 'Preview North' }).click()
  const card = page.getByRole('dialog', { name: 'Preview of North' })
  await expect(card).toContainText('Layla')
  await expect(card).toContainText('Open')
  await expect(card).not.toContainText('do-not-show')

  // The translatable status follows the language chosen in the Desk.
  await page.goto(`/app/${encodeURIComponent(parent)}`)
  await expect(page.getByRole('cell', { name: 'Open' })).toBeVisible()
  await page.evaluate(() => localStorage.setItem('qahwa.language', 'ar'))
  await page.reload()
  await expect(page.getByRole('cell', { name: 'مفتوح' })).toBeVisible()
  await page.evaluate(() => localStorage.removeItem('qahwa.language'))
})
