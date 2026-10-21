import { expect, test, type Page } from '@playwright/test'

const email = process.env.QAHWA_E2E_EMAIL
const password = process.env.QAHWA_E2E_PASSWORD

test.skip(!email || !password, 'Set QAHWA_E2E_EMAIL and QAHWA_E2E_PASSWORD to run the browser tests.')

async function signIn(page: Page) {
  await page.goto('/login')
  await page.getByLabel('Email').fill(email!)
  await page.getByLabel('Password').fill(password!)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('heading', { name: /^Welcome/ })).toBeVisible()
}

test('a wrong password stays on the sign-in page with a message', async ({ page }) => {
  await page.goto('/login')
  await page.getByLabel('Email').fill(email!)
  await page.getByLabel('Password').fill('definitely-not-the-password-1')
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page).toHaveURL(/\/login/)
  await expect(page.getByRole('alert')).toBeVisible()
})

test('signing in shows the home page grouped by module', async ({ page }) => {
  await signIn(page)
  await expect(page.getByRole('region', { name: 'Core' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Customer' }).first()).toBeVisible()
})

test('a customer made through the form appears in the list and can be opened', async ({ page }) => {
  await signIn(page)
  const name = `E2E Customer ${Date.now()}`
  await page.goto('/app/Customer/new')
  await page.getByLabel('Customer Name').fill(name)
  await page.getByRole('button', { name: /save/i }).first().click()
  await expect(page).toHaveURL(/\/app\/Customer\/[^/]+$/)
  await page.goto('/app/Customer')
  await expect(page.getByText(name)).toBeVisible()
})

test('unsaved typing comes back after a reload and can be discarded', async ({ page }) => {
  await signIn(page)
  await page.goto('/app/Customer/new')
  await page.getByLabel('Customer Name').fill('Half typed customer')
  await page.waitForTimeout(1200) // the draft is written shortly after typing stops
  await page.reload()
  await expect(page.getByText('Your unsaved changes were restored')).toBeVisible()
  await expect(page.getByLabel('Customer Name')).toHaveValue('Half typed customer')
  await page.getByRole('button', { name: 'Discard them' }).click()
  await expect(page.getByLabel('Customer Name')).toHaveValue('')
})

test('? lists the shortcuts and Alt+N starts a new document', async ({ page }) => {
  await signIn(page)
  await page.goto('/app/Customer')
  await page.keyboard.press('?')
  await expect(page.getByRole('heading', { name: 'Keyboard shortcuts' })).toBeVisible()
  await page.keyboard.press('Escape')
  await page.keyboard.press('Alt+n')
  await expect(page).toHaveURL(/\/app\/Customer\/new$/)
})
