import { describe, expect, it } from 'vitest'
import { MAX_FILE_BYTES, fileProblem, formatSize } from './files.ts'

describe('file helpers', () => {
  it('formats sizes', () => {
    expect(formatSize(0)).toBe('0 B')
    expect(formatSize(812)).toBe('812 B')
    expect(formatSize(1024)).toBe('1 KB')
    expect(formatSize(4608)).toBe('4.5 KB')
    expect(formatSize(12 * 1024 * 1024)).toBe('12 MB')
    expect(formatSize(3 * 1024 ** 3)).toBe('3 GB')
  })

  it('refuses empty and oversized files before sending', () => {
    expect(fileProblem({ name: 'a.txt', size: 10 })).toBeNull()
    expect(fileProblem({ name: 'a.txt', size: 0 })).toMatch(/empty/)
    expect(fileProblem({ name: 'big.bin', size: MAX_FILE_BYTES + 1 })).toMatch(/larger than 25 MB/)
    expect(fileProblem({ name: 'big.bin', size: 2 * 1024 * 1024 }, 1024 * 1024)).toMatch(/larger than 1 MB/)
  })
})
