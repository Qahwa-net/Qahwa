import { describe, expect, it } from 'vitest'
import type { DocField, DocTypeMeta } from '../api/types.ts'
import { csvCell, parseCsv, rowsFromCsv, rowsToCsv } from './tableCsv.ts'
import type { TableRow } from './tables.ts'

const field = (name: string, type = 'Data', extra: Partial<DocField> = {}): DocField => ({ name, type, label: name[0]!.toUpperCase() + name.slice(1), required: false, unique: false, ...extra } as unknown as DocField)
const child = { name: 'Line', fields: [field('item'), field('qty', 'Int'), field('note'), field('taxable', 'Check'), field('pin', 'Password', { readOnly: false })] } as unknown as DocTypeMeta
const row = (values: Record<string, string | boolean>, key = 'k'): TableRow => ({ key, values })

describe('table CSV', () => {
  it('writes a header of labels and one line per row, quoting what needs it', () => {
    const csv = rowsToCsv(child, [row({ item: 'Tea, green', qty: '2', note: 'say "hi"', taxable: true, pin: '' }), row({ item: 'Milk\nfresh', qty: '-3', note: '', taxable: false, pin: '' })])

    expect(csv.split('\r\n')[0]).toBe('Item,Qty,Note,Taxable,Pin')
    expect(csv).toContain('"Tea, green",2,"say ""hi""",Yes,')
    expect(csv).toContain('"Milk\nfresh",-3,,No,')
  })

  it('keeps spreadsheets from running a cell as a formula, but leaves negative numbers alone', () => {
    expect(csvCell('=HYPERLINK("x")')).toBe('"\'=HYPERLINK(""x"")"')
    expect(csvCell('+1 555')).toBe("'+1 555")
    expect(csvCell('-5')).toBe('-5')
    expect(csvCell('-5.25')).toBe('-5.25')
    expect(csvCell('@sum')).toBe("'@sum")
    expect(csvCell('plain')).toBe('plain')
  })

  it('reads what it wrote, including quotes, commas, line breaks and the formula guard', () => {
    const rows = [row({ item: 'Tea, green', qty: '2', note: 'say "hi"', taxable: true, pin: '' }), row({ item: '=cmd', qty: '', note: 'a\r\nb', taxable: false, pin: '' })]

    const back = rowsFromCsv(child, rowsToCsv(child, rows))

    expect(back.error).toBeUndefined()
    expect(back.rows.map((r) => r.values)).toEqual(rows.map((r) => r.values))
  })

  it('parses tolerant CSV: BOM, CRLF or LF, blank lines, a last line without a break', () => {
    expect(parseCsv('﻿a,b\r\n1,2\n\n3,4')).toEqual([['a', 'b'], ['1', '2'], ['3', '4']])
    expect(parseCsv('"a""b",,"c\nd"')).toEqual([['a"b', '', 'c\nd']])
    expect(parseCsv('')).toEqual([])
    expect(parseCsv('x,')).toEqual([['x', '']])
  })

  it('matches columns by label or field name, in any order, and reports what it ignored or lacked', () => {
    const result = rowsFromCsv(child, 'QTY,item,Colour\n5,Tea,red\n')

    expect(result.rows).toHaveLength(1)
    expect(result.rows[0]!.values).toMatchObject({ item: 'Tea', qty: '5' })
    expect(result.ignored).toEqual(['Colour'])
    expect(result.missing).toEqual(['Note', 'Taxable', 'Pin'])
  })

  it('reads yes and no for check boxes in the usual spellings', () => {
    const result = rowsFromCsv(child, 'item,taxable\na,Yes\nb,TRUE\nc,1\nd,no\ne,\nf,maybe\n')

    expect(result.rows.map((r) => r.values.taxable)).toEqual([true, true, true, false, false, false])
  })

  it('refuses a file that cannot be a table of this kind, an empty one, and too many rows', () => {
    expect(rowsFromCsv(child, 'x,y\n1,2\n').error).toMatch(/None of the columns/)
    expect(rowsFromCsv(child, '').error).toMatch(/empty/)
    expect(rowsFromCsv(child, 'item\n' + 'a\n'.repeat(11), 10).error).toMatch(/at most 10 rows/)
    expect(rowsFromCsv(child, 'item\n' + 'x'.repeat(2 * 1024 * 1024)).error).toMatch(/too large/)
  })

  it('skips empty lines and short lines', () => {
    const result = rowsFromCsv(child, 'item,qty\n,\nTea\n\n')

    expect(result.rows.map((r) => r.values.item)).toEqual(['Tea'])
    expect(result.rows[0]!.values.qty).toBe('')
  })
})
