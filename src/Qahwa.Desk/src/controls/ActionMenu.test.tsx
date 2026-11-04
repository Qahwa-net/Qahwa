// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ActionMenu, type MenuItem } from './ActionMenu.tsx'

Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
let container: HTMLDivElement, root: Root

beforeEach(() => { container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
afterEach(() => { act(() => root.unmount()); container.remove() })

const render = (items: MenuItem[]) => act(() => root.render(<ActionMenu items={items} />))
const button = () => container.querySelector<HTMLButtonElement>('button[aria-haspopup="menu"]')!
const choices = () => [...container.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')]
const press = (target: Element, key: string) => act(() => { target.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true })) })

describe('ActionMenu', () => {
  it('draws nothing when there are no actions', () => {
    render([])
    expect(container.innerHTML).toBe('')
  })

  it('opens on click, runs the chosen action and closes', () => {
    const duplicate = vi.fn()
    render([{ label: 'Duplicate', onSelect: duplicate }, { label: 'Delete', tone: 'danger', onSelect: () => {} }])
    expect(button().getAttribute('aria-expanded')).toBe('false')
    expect(choices()).toHaveLength(0)

    act(() => button().click())
    expect(button().getAttribute('aria-expanded')).toBe('true')
    expect(choices().map(c => c.textContent)).toEqual(['Duplicate', 'Delete'])
    expect(choices()[1]!.className).toBe('danger')

    act(() => choices()[0]!.click())
    expect(duplicate).toHaveBeenCalledOnce()
    expect(choices()).toHaveLength(0)
  })

  it('moves with the arrow keys, skips disabled actions and wraps around', () => {
    render([{ label: 'One', onSelect: () => {} }, { label: 'Two', disabled: true, onSelect: () => {} }, { label: 'Three', onSelect: () => {} }])
    act(() => button().click())
    expect(document.activeElement).toBe(choices()[0])
    press(document.activeElement!, 'ArrowDown')
    expect(document.activeElement).toBe(choices()[2])
    press(document.activeElement!, 'ArrowDown')
    expect(document.activeElement).toBe(choices()[0])
    press(document.activeElement!, 'ArrowUp')
    expect(document.activeElement).toBe(choices()[2])
    press(document.activeElement!, 'Home')
    expect(document.activeElement).toBe(choices()[0])
  })

  it('closes on Escape and gives the focus back to the button', () => {
    render([{ label: 'One', onSelect: () => {} }])
    act(() => button().click())
    press(document.activeElement!, 'Escape')
    expect(choices()).toHaveLength(0)
    expect(document.activeElement).toBe(button())
  })

  it('closes when the page is clicked elsewhere', () => {
    render([{ label: 'One', onSelect: () => {} }])
    act(() => button().click())
    act(() => { document.body.dispatchEvent(new MouseEvent('mousedown', { bubbles: true })) })
    expect(choices()).toHaveLength(0)
  })

  it('shows a download as a link and closes after it is followed', () => {
    render([{ label: 'Export CSV', href: '/api/resource/Customer/export' }])
    act(() => button().click())
    const link = container.querySelector<HTMLAnchorElement>('a[role="menuitem"]')!
    expect(link.getAttribute('href')).toBe('/api/resource/Customer/export')
    expect(link.hasAttribute('download')).toBe(true)
    link.addEventListener('click', e => e.preventDefault())
    act(() => link.click())
    expect(choices()).toHaveLength(0)
  })
})
