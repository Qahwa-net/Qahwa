import { useEffect, useRef } from 'react'
import { parseEvent, realtimeUrl, type LiveEvent } from './realtime.ts'

/**
 * Listens to the server's event stream (the person's own, or one document's) while the component is shown. The browser reconnects by
 * itself after a break; a missing or refused stream is not an error (the page still works, it just updates when you do something).
 */
export function useLive(onEvent: (event: LiveEvent) => void, doctype?: string, name?: string, enabled = true): void {
  const handler = useRef(onEvent)
  useEffect(() => { handler.current = onEvent })

  useEffect(() => {
    if (!enabled || typeof EventSource === 'undefined') return
    const source = new EventSource(realtimeUrl(doctype, name))
    const listen = (type: string) => source.addEventListener(type, (e) => {
      const event = parseEvent(type, (e as MessageEvent<string>).data)
      if (event) handler.current(event)
    })
    listen('notification')
    listen('doc')
    return () => source.close()
  }, [doctype, name, enabled])
}
