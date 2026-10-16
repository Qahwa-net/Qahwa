import { useCallback, useEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client.ts'
import type { DocField, DocTypeMeta } from '../api/types.ts'
import { ClientScriptRunner, type ScriptOp } from './clientScripts.ts'

export interface FieldOverride {
  hidden?: boolean
  read_only?: boolean
  reqd?: boolean
}

export interface ScriptButton {
  id: string
  label: string
}

interface Options {
  meta: DocTypeMeta
  /** The document as the scripts see it (typed values, no passwords or tables). */
  getDoc: () => Record<string, unknown>
  /** The values on the form, to notice which field the person changed. */
  values: Record<string, unknown>
  /** Puts a value a script set into the form. */
  applyValue: (field: DocField, value: unknown) => void
  /** Changes when the document was saved or reloaded, so "refresh" runs again. */
  version: string | number | null
}

/**
 * Client scripts for a form: the scripts of the DocType run in a sandbox (a worker), get the events "onload", "refresh", "validate"
 * and one per changed field, and can ask the form to set values, hide, require or lock fields, show a note and add buttons.
 * The form stays usable if a script fails or loops; the scripts just stop for that form.
 */
export function useClientScripts({ meta, getDoc, values, applyValue, version }: Options) {
  const query = useQuery({ queryKey: ['client-scripts', meta.name], queryFn: () => api.clientScripts(meta.name), staleTime: 60_000, retry: false })
  const [overrides, setOverrides] = useState<Record<string, FieldOverride>>({})
  const [intro, setIntro] = useState<string | null>(null)
  const [buttons, setButtons] = useState<ScriptButton[]>([])
  const [notice, setNotice] = useState<string | null>(null)

  const runner = useRef<ClientScriptRunner | null>(null)
  const chain = useRef<Promise<unknown>>(Promise.resolve())
  const getDocRef = useRef(getDoc)
  const applyRef = useRef(applyValue)
  const scriptSet = useRef(new Set<string>())
  const previous = useRef<Record<string, unknown> | null>(null)
  const lastVersion = useRef(version)
  getDocRef.current = getDoc
  applyRef.current = applyValue

  const enqueue = useCallback(<T,>(task: () => Promise<T>): Promise<T> => {
    const next = chain.current.then(task, task)
    chain.current = next.catch(() => undefined)
    return next
  }, [])

  /** Does what the scripts asked. Returns the message of the first refusal, if any. */
  const apply = useCallback((ops: ScriptOp[], resetUi: boolean): string | null => {
    let refusal: string | null = null
    const collected: ScriptButton[] = []
    let newIntro: string | null | undefined
    for (const op of ops) {
      switch (op.op) {
        case 'set_value': {
          const field = meta.fields.find((f) => f.name === op.field)
          if (field) {
            scriptSet.current.add(field.name)
            applyRef.current(field, op.value)
          }
          break
        }
        case 'set_df_property':
          setOverrides((o) => ({ ...o, [op.field]: { ...o[op.field], [op.property]: op.value } }))
          break
        case 'set_intro':
          newIntro = op.text === '' ? null : op.text
          break
        case 'button':
          if (!collected.some((b) => b.id === op.id)) collected.push({ id: op.id, label: op.label })
          break
        case 'msgprint':
          setNotice(op.text)
          break
        case 'error':
          setNotice(`A client script has a problem: ${op.message}`)
          break
        case 'throw':
          refusal ??= op.message
          break
      }
    }

    if (resetUi) {
      setIntro(newIntro ?? null)
      setButtons(collected)
    } else {
      if (newIntro !== undefined) setIntro(newIntro)
      if (collected.length > 0) setButtons((b) => [...b, ...collected.filter((c) => !b.some((x) => x.id === c.id))])
    }

    if (runner.current?.stopped && runner.current.stopped !== 'Closed.') setNotice(runner.current.stopped)
    return refusal
  }, [meta])

  const runEvent = useCallback(async (event: string, resetUi = false): Promise<string | null> => {
    const r = runner.current
    if (!r) return null
    return apply(await r.run(event, getDocRef.current()), resetUi)
  }, [apply])

  // Start the sandbox when the scripts arrive; end it with the form.
  const scripts = query.data
  useEffect(() => {
    if (!scripts || scripts.length === 0) return
    const r = new ClientScriptRunner(meta)
    runner.current = r
    previous.current = null
    let alive = true
    void enqueue(async () => {
      const errors = await r.load(scripts)
      if (!alive) return
      if (errors.length > 0) setNotice(errors.join(' '))
      await runEvent('onload')
      await runEvent('refresh', true)
    })
    return () => {
      alive = false
      r.dispose()
      if (runner.current === r) runner.current = null
      setOverrides({})
      setIntro(null)
      setButtons([])
    }
  }, [scripts, meta, enqueue, runEvent])

  // One event for each field the person changed (not for values a script set itself, which would loop).
  useEffect(() => {
    const before = previous.current
    previous.current = { ...values }
    if (!before || !runner.current) return
    const changed = Object.keys(values).filter((k) => values[k] !== before[k])
    for (const key of changed) {
      if (scriptSet.current.delete(key)) continue
      void enqueue(() => runEvent(key))
    }
  }, [values, enqueue, runEvent])

  // After a save the form shows the saved document: "refresh" again.
  useEffect(() => {
    if (lastVersion.current === version) return
    lastVersion.current = version
    if (runner.current) void enqueue(() => runEvent('refresh', true))
  }, [version, enqueue, runEvent])

  /** Runs the "validate" scripts. Returns the reason to stop the save, or null. */
  const beforeSave = useCallback((): Promise<string | null> => (runner.current ? enqueue(() => runEvent('validate')) : Promise.resolve(null)), [enqueue, runEvent])

  const runButton = useCallback((id: string) => {
    void enqueue(async () => {
      const r = runner.current
      if (!r) return
      const refusal = apply(await r.runButton(id, getDocRef.current()), false)
      if (refusal) setNotice(refusal)
    })
  }, [enqueue, apply])

  return { overrides, intro, buttons, notice, dismissNotice: () => setNotice(null), beforeSave, runButton }
}
