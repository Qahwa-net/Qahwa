import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client.ts'

/**
 * What the installed apps handed the Desk at start-up (`GET /api/boot`): one object per app that has a boot hook. Fetched once per
 * session; `undefined` until it arrives, and an empty object when no app has one.
 */
export function useBoot(): Record<string, unknown> | undefined {
  return useQuery({ queryKey: ['boot'], queryFn: api.boot, staleTime: Infinity, retry: false }).data
}

/** One app's boot object, typed by the caller (the shape is the app's own). */
export function useAppBoot<T>(app: string): T | undefined {
  return useBoot()?.[app] as T | undefined
}
