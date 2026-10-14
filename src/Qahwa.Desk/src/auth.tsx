import { createContext, useContext, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './api/client.ts'
import type { SessionUser } from './api/types.ts'

interface AuthState {
  /** Undefined while the first check is in flight, null when signed out. */
  user: SessionUser | null | undefined
  login: (email: string, password: string) => Promise<void>
  logout: () => Promise<void>
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const me = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: Infinity })

  const login = useMutation({
    mutationFn: ({ email, password }: { email: string; password: string }) => api.login(email, password),
    onSuccess: (user) => {
      queryClient.clear() // never show one user's cached data to the next
      queryClient.setQueryData(['me'], user)
    },
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
    login: async (email, password) => { await login.mutateAsync({ email, password }) },
    logout: async () => { await logout.mutateAsync() },
  }

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthState {
  const context = useContext(AuthContext)
  if (!context) throw new Error('useAuth must be used inside AuthProvider')
  return context
}
