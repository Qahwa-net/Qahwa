import { useCallback, useEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client.ts'
import type { DocTypeMeta, IndicatorColor } from '../api/types.ts'
import { ClientScriptRunner, type ScriptOp } from './clientScripts.ts'

export interface ListScriptButton {
  id: string
  label: string
}

interface Options {
  meta: DocTypeMeta | undefined
  /** The rows on the page now (a copy goes to the scripts). */
  rows: Record<string, unknown>[] | undefined
  /** The names ticked. */
  selected: string[]
  /** Puts a filter a script asked for into the list ("field = value"). */
  setFilter: (field: string, value: string | number | boolean) => void
}

/**
 * Client scripts for a list: the same sandbox as the form's scripts. They get "onload" once and "refresh" whenever the rows on the
 * page change, can add buttons (which get the rows and the ticked names), a note above the list, a colour for a row, and (from
 * "onload" or a button, never from "refresh", so they cannot loop) a filter.
 */
export function useListScripts({ meta, rows, selected, setFilter }: Options) {
  const query = useQuery({ queryKey: ['client-scripts', meta?.name], queryFn: () => api.clientScripts(meta!.name), enabled: Boolean(meta), staleTime: 60_000, retry: false })
  const [buttons, setButtons] = useState<ListScriptButton[]>([])
  const [intro, setIntro] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [indicators, setIndicators] = useState<Record<string, IndicatorColor>>({})

  const runner = useRef<ClientScriptRunner | null>(null)
  const chain = useRef<Promise<unknown>>(Promise.resolve())
  const latest = useRef({ rows, selected, setFilter })
  latest.current = { rows, selected, setFilter }

  const enqueue = useCallback(<T,>(task: () => Promise<T>): Promise<T> => {
    const next = chain.current.then(task, task)
    chain.current = next.catch(() => undefined)
    return next
  }, [])

  const apply = useCallback((ops: ScriptOp[], fromRefresh: boolean) => {
    const collected: ListScriptButton[] = []
    const colours: Record<string, IndicatorColor> = {}
    let newIntro: string | null | undefined
    for (const op of ops) {
      if (op.op === 'button' && !collected.some((b) => b.id === op.id)) collected.push({ id: op.id, label: op.label })
      else if (op.op === 'set_intro') newIntro = op.text === '' ? null : op.text
      else if (op.op === 'msgprint') setNotice(op.text)
      else if (op.op === 'error') setNotice(`A client script has a problem: ${op.message}`)
      else if (op.op === 'set_row_indicator') colours[op.name] = op.color
      else if (op.op === 'set_filter' && !fromRefresh) latest.current.setFilter(op.field, op.value)
    }

    if (fromRefresh) {
      setButtons(collected)
      setIntro(newIntro ?? null)
      setIndicators(colours)
    } else {
      if (collected.length > 0) setButtons((b) => [...b, ...collected.filter((c) => !b.some((x) => x.id === c.id))])
      if (newIntro !== undefined) setIntro(newIntro)
      if (Object.keys(colours).length > 0) setIndicators((i) => ({ ...i, ...colours }))
    }

    if (runner.current?.stopped && runner.current.stopped !== 'Closed.') setNotice(runner.current.stopped)
  }, [])

  const scripts = query.data
  useEffect(() => {
    if (!meta || !scripts || scripts.length === 0) return
    const r = new ClientScriptRunner(meta)
    runner.current = r
    let alive = true
    void enqueue(async () => {
      const errors = await r.load(scripts)
      if (!alive) return
      if (errors.length > 0) setNotice(errors.join(' '))
      apply(await r.runList('onload', latest.current.rows ?? [], latest.current.selected), false)
    })
    return () => {
      alive = false
      r.dispose()
      if (runner.current === r) runner.current = null
      setButtons([])
      setIntro(null)
      setIndicators({})
    }
  }, [scripts, meta, enqueue, apply])

  // Whenever the rows on the page change, "refresh" runs again.
  useEffect(() => {
    if (!rows || !runner.current) return
    void enqueue(async () => {
      const r = runner.current
      if (r) apply(await r.runList('refresh', latest.current.rows ?? [], latest.current.selected), true)
    })
  }, [rows, enqueue, apply])

  const runButton = useCallback((id: string) => {
    void enqueue(async () => {
      const r = runner.current
      if (r) apply(await r.runListButton(id, latest.current.rows ?? [], latest.current.selected), false)
    })
  }, [enqueue, apply])

  return { buttons, intro, notice, indicators, dismissNotice: () => setNotice(null), runButton }
}
