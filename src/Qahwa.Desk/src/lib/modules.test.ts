import { describe, expect, it } from 'vitest'
import { groupByModule } from './modules.ts'

const items = [{ name: 'Quote', module: 'Selling' }, { name: 'Customer', module: 'Selling' }, { name: 'Item', module: 'Stock' }, { name: 'Loose' }]

describe('groupByModule', () => {
  it('groups, sorts by name and puts un-named modules last', () => {
    const groups = groupByModule(items, [])
    expect(groups.map((g) => g.label)).toEqual(['Selling', 'Stock', 'Other'])
    expect(groups[0]!.items.map((i) => i.name)).toEqual(['Customer', 'Quote'])
  })
  it('uses the position, label and hidden flag of a definition', () => {
    const groups = groupByModule(items, [
      { module_name: 'Stock', label: 'Warehouse', sort_order: 1 },
      { module_name: 'Selling', sort_order: 2, description: 'Sales' },
      { module_name: 'Other', hidden: true },
    ])
    expect(groups.map((g) => g.label)).toEqual(['Warehouse', 'Selling', 'Other'])
    expect(groups[1]!.description).toBe('Sales')
    expect(groupByModule(items, [{ module_name: 'Stock', hidden: true }]).map((g) => g.module)).toEqual(['Selling', ''])
  })
})
