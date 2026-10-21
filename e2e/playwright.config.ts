import { defineConfig } from '@playwright/test'

/**
 * End-to-end tests of the Desk in a real browser against a running Qahwa site (ADR-115). They need a site you can throw away:
 *   QAHWA_E2E_URL       the address (default http://localhost:5099)
 *   QAHWA_E2E_EMAIL     an administrator of that site
 *   QAHWA_E2E_PASSWORD  that administrator's password (never put it in a file; set it in the environment)
 * Without the email and password every test is skipped.
 */
export default defineConfig({
  testDir: './tests',
  timeout: 30_000,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: process.env.QAHWA_E2E_URL ?? 'http://localhost:5099',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
})
