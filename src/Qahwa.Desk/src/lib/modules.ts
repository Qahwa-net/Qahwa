export interface ModuleDef {
  module_name: string
  label?: string | null
  sort_order?: number | null
  description?: string | null
  hidden?: boolean | number | null
}

export interface ModuleGroup<T> {
  module: string
  label: string
  description: string
  items: T[]
}

/** Groups DocTypes by module: ordered by the Module Definition position then name; hidden modules are left out; modules without a definition come last as "Other" when they have no name. */
export function groupByModule<T extends { name: string; module?: string | null }>(items: T[], defs: ModuleDef[]): ModuleGroup<T>[] {
  const byName = new Map(defs.map((d) => [d.module_name, d]))
  const groups = new Map<string, T[]>()
  for (const item of items) {
    const key = item.module?.trim() || ''
    groups.set(key, [...(groups.get(key) ?? []), item])
  }
  return [...groups.entries()]
    .filter(([key]) => !byName.get(key)?.hidden)
    .map(([key, list]) => {
      const def = byName.get(key)
      return {
        module: key,
        label: def?.label?.trim() || key || 'Other',
        description: def?.description ?? '',
        items: [...list].sort((a, b) => a.name.localeCompare(b.name)),
        order: def?.sort_order ?? (key === '' ? Number.MAX_SAFE_INTEGER : 100),
      }
    })
    .sort((a, b) => a.order - b.order || a.label.localeCompare(b.label))
    .map(({ order: _order, ...group }) => group)
}
