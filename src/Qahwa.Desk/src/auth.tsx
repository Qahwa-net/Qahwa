import { createContext, useContext, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './api/client.ts'
import type { SessionUser } from './api/types.ts'

interface AuthState {
  /** Undefined while the first check is in flight, null when signed out. */
  user: SessionUser | null | undefined
  /** Resolves to a challenge when the account needs a code too (then call loginWithCode), or null once signed in. */
  login: (email: string, password: string) => Promise<{ token: string; kind: 'code' | 'expired' } | null>
  loginWithCode: (token: string, code: string) => Promise<void>
  logout: () => Promise<void>
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const me = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: Infinity })

  const signedIn = (user: SessionUser) => {
    queryClient.clear() // never show one user's cached data to the next
    queryClient.setQueryData(['me'], user)
  }

  const login = useMutation({
    mutationFn: ({ email, password }: { email: string; password: string }) => api.login(email, password),
    onSuccess: (result) => { if (!('two_factor' in result) && !('password_expired' in result)) signedIn(result) },
  })

  const loginWithCode = useMutation({
    mutationFn: ({ token, code }: { token: string; code: string }) => api.loginWithCode(token, code),
    onSuccess: signedIn,
  })

  const logout = useMutation({
    mutationFn: api.logout,
    onSettled: () => {
      queryClient.clear()
      queryClient.setQueryData(['me'], null)
    },
  })

  const value: AuthState = {
    user: me.isPending ? undefined : (me.data ?? null),
    login: async (email, password) => {
      const result = await login.mutateAsync({ email, password })
      return 'two_factor' in result ? { token: result.token, kind: 'code' } : 'password_expired' in result ? { token: result.token, kind: 'expired' } : null
    },
    loginWithCode: async (token, code) => { await loginWithCode.mutateAsync({ token, code }) },
    logout: async () => { await logout.mutateAsync() },
  }

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthState {
  const context = useContext(AuthContext)
  if (!context) throw new Error('useAuth must be used inside AuthProvider')
  return context
}
