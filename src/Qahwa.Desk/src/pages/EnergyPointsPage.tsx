import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '../api/client.ts'

const PERIODS = [['week', 'This week'], ['month', 'This month'], ['all', 'All time']] as const

/** My energy points and who has the most: recognition for work done in the system. */
export function EnergyPointsPage() {
  const [period, setPeriod] = useState<(typeof PERIODS)[number][0]>('week')
  const mine = useQuery({ queryKey: ['energy-points'], queryFn: api.energyPoints.mine, retry: false })
  const board = useQuery({ queryKey: ['energy-points', 'leaderboard', period], queryFn: () => api.energyPoints.leaderboard(period), retry: false })
  return (
    <>
      <h1>Energy points</h1>
      <p className="muted">Points are given for work you do in the system, by rules your administrator sets (for example for raising or closing a ticket).</p>
      {mine.data && (
        <ul className="tiles">
          {([['Total', mine.data.total], ['This week', mine.data.thisWeek], ['This month', mine.data.thisMonth]] as const).map(([label, value]) => (
            <li key={label} className="card tile"><strong>{value}</strong><span className="muted">{label}</span></li>
          ))}
        </ul>
      )}

      <h2>Leaderboard</h2>
      <div className="tabs" role="tablist" aria-label="Period">
        {PERIODS.map(([key, label]) => <button key={key} type="button" role="tab" aria-selected={period === key} className={period === key ? 'on' : undefined} onClick={() => setPeriod(key)}>{label}</button>)}
      </div>
      {board.isPending && <p className="muted" role="status">Loading…</p>}
      {board.data && board.data.length === 0 && <p className="muted">Nobody has points {period === 'all' ? 'yet' : 'in this period'}.</p>}
      {board.data && board.data.length > 0 && (
        <ol className="plain-list leaderboard">
          {board.data.map((entry, i) => (
            <li key={`${entry.fullName}-${i}`} className={entry.you ? 'you' : undefined}><strong>{i + 1}.</strong> {entry.fullName}{entry.you && <span className="chip"> you</span>} <span className="muted">· {entry.points} points</span></li>
          ))}
        </ol>
      )}

      <h2>Your latest points</h2>
      {mine.data && mine.data.recent.length === 0 && <p className="muted">You have not earned points yet.</p>}
      <ul className="plain-list">
        {(mine.data?.recent ?? []).map((e, i) => (
          <li key={`${e.at}-${i}`}>
            <strong className={e.points < 0 ? 'neg' : undefined}>{e.points > 0 ? `+${e.points}` : e.points}</strong> {e.docType && e.name ? <Link to={`/app/${encodeURIComponent(e.docType)}/${encodeURIComponent(e.name)}`}>{e.reason}</Link> : e.reason}
            <small className="muted"> · {new Date(e.at).toLocaleString()}</small>
          </li>
        ))}
      </ul>
    </>
  )
}
