import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { QueryCache, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import './styles.css'
import { ApiError } from './api/client.ts'
import { App } from './App.tsx'
import { I18nProvider } from './i18n.tsx'
import { ToastProvider } from './controls/Toast.tsx'
import { ThemeProvider } from './theme.tsx'

const queryClient: QueryClient = new QueryClient({
  // When the session ends (expired, disabled, signed out elsewhere) any request returns 401: drop to the login page.
  queryCache: new QueryCache({
    onError: (error) => {
      if (error instanceof ApiError && error.status === 401) queryClient.setQueryData(['me'], null)
    },
  }),
  defaultOptions: {
    queries: {
      retry: (count, error) => !(error instanceof ApiError) && count < 2,
      refetchOnWindowFocus: false,
      staleTime: 10_000,
    },
  },
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <ThemeProvider>
        <I18nProvider>
          <ToastProvider>
            <BrowserRouter>
              <App />
            </BrowserRouter>
          </ToastProvider>
        </I18nProvider>
      </ThemeProvider>
    </QueryClientProvider>
  </StrictMode>,
)
