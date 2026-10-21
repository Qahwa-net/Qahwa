import { request, type FullConfig } from '@playwright/test'
import { mkdirSync } from 'node:fs'

/** Signs in once for the whole run (the site limits sign-ins per minute) and keeps the session for the tests. */
export default async function globalSetup(config: FullConfig) {
  const email = process.env.QAHWA_E2E_EMAIL
  const password = process.env.QAHWA_E2E_PASSWORD
  if (!email || !password) return
  const baseURL = config.projects[0]!.use.baseURL!
  const api = await request.newContext({ baseURL })
  const response = await api.post('/api/auth/login', { data: { email, password } })
  if (!response.ok()) throw new Error(`Could not sign in to ${baseURL} (${response.status()}). Check QAHWA_E2E_EMAIL and QAHWA_E2E_PASSWORD.`)
  mkdirSync('.auth', { recursive: true })
  await api.storageState({ path: '.auth/state.json' })
  await api.dispose()
}
