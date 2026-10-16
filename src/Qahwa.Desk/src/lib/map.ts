import { parsePlace } from './fieldtypes.ts'

/**
 * A plain map of places: points on a grid of latitude and longitude, fitted to the places shown. There is no background picture on
 * purpose: loading map tiles would send every viewer's activity to a third party and needs a looser content policy. Each place links to
 * a full map instead.
 */

export interface MapInput {
  name: string
  title: string
  place: unknown
}

export interface MapPoint {
  name: string
  title: string
  lat: number
  lng: number
  /** Percentages of the width and height of the plot. */
  x: number
  y: number
}

export interface MapGridLine {
  label: string
  at: number
  vertical: boolean
}

export interface MapLayout {
  points: MapPoint[]
  skipped: number
  grid: MapGridLine[]
  bounds: { minLat: number; maxLat: number; minLng: number; maxLng: number } | null
}

const MIN_SPAN = 0.02 // degrees: one place (or several in one spot) still gets a sensible view

/** A grid step that gives about 3 to 6 lines across the span: 0.01, 0.02, 0.05, 0.1 ... 20, 50. */
export function gridStep(span: number): number {
  const target = span / 4
  const base = 10 ** Math.floor(Math.log10(target))
  for (const m of [1, 2, 5, 10]) if (m * base >= target) return m * base
  return base * 10
}

const round = (n: number) => Math.round(n * 1e6) / 1e6

export function layoutMap(items: MapInput[]): MapLayout {
  const located = items.flatMap((i) => {
    const place = parsePlace(i.place)
    return place ? [{ i, place }] : []
  })
  const skipped = items.length - located.length
  if (located.length === 0) return { points: [], skipped, grid: [], bounds: null }

  const lats = located.map((l) => l.place.lat)
  const lngs = located.map((l) => l.place.lng)
  let [minLat, maxLat, minLng, maxLng] = [Math.min(...lats), Math.max(...lats), Math.min(...lngs), Math.max(...lngs)]
  // Keep both axes the same scale (a degree is a degree), with a margin so no point sits on the edge.
  const span = Math.max(maxLat - minLat, maxLng - minLng, MIN_SPAN)
  const pad = span * 0.12
  const midLat = (minLat + maxLat) / 2
  const midLng = (minLng + maxLng) / 2
  ;[minLat, maxLat, minLng, maxLng] = [midLat - span / 2 - pad, midLat + span / 2 + pad, midLng - span / 2 - pad, midLng + span / 2 + pad]
  const full = span + 2 * pad

  const points = located.map(({ i, place }) => ({
    name: i.name, title: i.title || i.name, lat: place.lat, lng: place.lng,
    x: ((place.lng - minLng) / full) * 100, y: ((maxLat - place.lat) / full) * 100,
  }))

  const step = gridStep(full)
  const grid: MapGridLine[] = []
  for (let lat = Math.ceil(minLat / step) * step; lat <= maxLat; lat += step) grid.push({ label: String(round(lat)), at: ((maxLat - lat) / full) * 100, vertical: false })
  for (let lng = Math.ceil(minLng / step) * step; lng <= maxLng; lng += step) grid.push({ label: String(round(lng)), at: ((lng - minLng) / full) * 100, vertical: true })
  return { points, skipped, grid, bounds: { minLat, maxLat, minLng, maxLng } }
}
