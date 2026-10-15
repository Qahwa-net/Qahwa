import type { Point } from '../lib/charts.ts'
import { barHeights, formatNumber, niceMax, pieSlices, slicePath } from '../lib/charts.ts'

const W = 360
const H = 200
const PAD = { left: 40, right: 8, top: 10, bottom: 34 }

const label = (p: Point) => p.label ?? '(empty)'
const short = (text: string) => (text.length > 10 ? `${text.slice(0, 9)}…` : text)

/** The same numbers as a table for screen readers; the picture is decoration. */
function DataTable({ title, points }: { title: string; points: Point[] }) {
  return (
    <table className="sr-only">
      <caption>{title}</caption>
      <tbody>{points.map((p, i) => <tr key={i}><th scope="row">{label(p)}</th><td>{p.value}</td></tr>)}</tbody>
    </table>
  )
}

function Axis({ top }: { top: number }) {
  const ticks = [0, 0.25, 0.5, 0.75, 1]
  return (
    <>
      {ticks.map((t) => {
        const y = PAD.top + (H - PAD.top - PAD.bottom) * (1 - t)
        return (
          <g key={t}>
            <line x1={PAD.left} x2={W - PAD.right} y1={y} y2={y} className="chart-grid" />
            <text x={PAD.left - 5} y={y + 3} textAnchor="end" className="chart-axis">{formatNumber(top * t, true)}</text>
          </g>
        )
      })}
    </>
  )
}

/** A bar chart. Hovering a bar shows its value; the table below is for assistive technology. */
export function BarChart({ points, title }: { points: Point[]; title: string }) {
  const plot = H - PAD.top - PAD.bottom
  const heights = barHeights(points.map((p) => p.value), plot)
  const top = niceMax(Math.max(0, ...points.map((p) => p.value)))
  const slot = (W - PAD.left - PAD.right) / Math.max(points.length, 1)
  const every = Math.ceil(points.length / 8)
  return (
    <>
      <svg viewBox={`0 0 ${W} ${H}`} className="chart" role="img" aria-label={title}>
        <Axis top={top} />
        {points.map((p, i) => (
          <g key={i}>
            <rect x={PAD.left + slot * i + slot * 0.15} y={PAD.top + plot - heights[i]!} width={slot * 0.7} height={heights[i]} className="chart-bar" rx="2">
              <title>{`${label(p)}: ${formatNumber(p.value)}`}</title>
            </rect>
            {i % every === 0 && <text x={PAD.left + slot * i + slot / 2} y={H - 14} textAnchor="middle" className="chart-axis">{short(label(p))}</text>}
          </g>
        ))}
      </svg>
      <DataTable title={title} points={points} />
    </>
  )
}

/** A line chart for values over time. */
export function LineChart({ points, title }: { points: Point[]; title: string }) {
  const plot = H - PAD.top - PAD.bottom
  const heights = barHeights(points.map((p) => p.value), plot)
  const top = niceMax(Math.max(0, ...points.map((p) => p.value)))
  const step = (W - PAD.left - PAD.right) / Math.max(points.length - 1, 1)
  const xy = points.map((_, i) => [PAD.left + (points.length === 1 ? (W - PAD.left - PAD.right) / 2 : step * i), PAD.top + plot - heights[i]!] as const)
  const every = Math.ceil(points.length / 8)
  return (
    <>
      <svg viewBox={`0 0 ${W} ${H}`} className="chart" role="img" aria-label={title}>
        <Axis top={top} />
        <polyline className="chart-line" points={xy.map(([x, y]) => `${x},${y}`).join(' ')} fill="none" />
        {xy.map(([x, y], i) => (
          <g key={i}>
            <circle cx={x} cy={y} r="3.5" className="chart-dot"><title>{`${label(points[i]!)}: ${formatNumber(points[i]!.value)}`}</title></circle>
            {i % every === 0 && <text x={x} y={H - 14} textAnchor="middle" className="chart-axis">{short(label(points[i]!))}</text>}
          </g>
        ))}
      </svg>
      <DataTable title={title} points={points} />
    </>
  )
}

/** A pie chart with a legend; each slice has its share in its tooltip. */
export function PieChart({ points, title }: { points: Point[]; title: string }) {
  const slices = pieSlices(points.map((p) => p.value))
  return (
    <>
      <div className="pie-wrap">
        <svg viewBox="0 0 120 120" className="chart pie" role="img" aria-label={title}>
          {slices.map((s, i) => s.share > 0 && (
            <path key={i} d={slicePath(60, 60, 56, s.start, s.end)} className={`slice slice-${i % 8}`}>
              <title>{`${label(points[i]!)}: ${formatNumber(points[i]!.value)} (${Math.round(s.share * 100)}%)`}</title>
            </path>
          ))}
        </svg>
        <ul className="legend">
          {points.map((p, i) => <li key={i}><span className={`swatch slice-${i % 8}`} /> {label(p)} <small className="muted">{formatNumber(p.value)}</small></li>)}
        </ul>
      </div>
      <DataTable title={title} points={points} />
    </>
  )
}
