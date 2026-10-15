export interface Point {
  label: string | null
  value: number
}

/** A round number at or above the largest value, for the top of an axis: 7 → 8, 130 → 150, 0 → 1. */
export function niceMax(max: number): number {
  if (!(max > 0)) return 1
  const magnitude = 10 ** Math.floor(Math.log10(max))
  const unit = [1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10].find((n) => n * magnitude >= max) ?? 10
  return unit * magnitude
}

/** Where each value sits on a vertical axis of height <paramref name="height"/> that starts at zero (negative values are drawn as zero height). */
export function barHeights(values: number[], height: number): number[] {
  const top = niceMax(Math.max(0, ...values))
  return values.map((v) => (v > 0 ? (v / top) * height : 0))
}

/** The slices of a pie: each point's share of the total, as a start and end angle in radians from 12 o'clock. Zero and negative values get no slice. */
export function pieSlices(values: number[]): { start: number; end: number; share: number }[] {
  const positive = values.map((v) => (v > 0 ? v : 0))
  const total = positive.reduce((a, b) => a + b, 0)
  let angle = 0
  return positive.map((v) => {
    const share = total === 0 ? 0 : v / total
    const slice = { start: angle, end: angle + share * Math.PI * 2, share }
    angle = slice.end
    return slice
  })
}

/** The SVG path of one slice (or a full circle when it takes everything). */
export function slicePath(cx: number, cy: number, r: number, start: number, end: number): string {
  if (end - start >= Math.PI * 2 - 1e-9) return `M ${cx - r} ${cy} A ${r} ${r} 0 1 1 ${cx + r} ${cy} A ${r} ${r} 0 1 1 ${cx - r} ${cy} Z`
  const point = (a: number) => `${(cx + r * Math.sin(a)).toFixed(2)} ${(cy - r * Math.cos(a)).toFixed(2)}`
  return `M ${cx} ${cy} L ${point(start)} A ${r} ${r} 0 ${end - start > Math.PI ? 1 : 0} 1 ${point(end)} Z`
}

/** "1,234.5", and a compact "12.3K" or "4.5M" for axis labels and big numbers. */
export function formatNumber(value: number, compact = false): string {
  if (compact && Math.abs(value) >= 1_000_000) return `${trim(value / 1_000_000)}M`
  if (compact && Math.abs(value) >= 1_000) return `${trim(value / 1_000)}K`
  return Number.isInteger(value) ? value.toLocaleString('en-US') : value.toLocaleString('en-US', { maximumFractionDigits: 2 })
}

const trim = (n: number) => String(Math.round(n * 10) / 10)
