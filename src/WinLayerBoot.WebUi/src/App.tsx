import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api, streamStatus, type JobInfo, type OpInfo } from './api'
import Overview from './pages/Overview'
import OpPage from './pages/OpPage'
import Entries from './pages/Entries'
import Settings from './pages/Settings'
import { Icon } from './icons'
import { elapsed } from './job'

// Same paging as the WinUI window: overview / make (base disk, child disk, snapshot, export, monitor) / manage (boot entries) / settings
const NAV: { tag: string; label: string; group?: string }[] = [
  { tag: 'overview', label: 'Overview' },
  { tag: 'base', label: 'Base disk', group: 'Create' },
  { tag: 'layer', label: 'Child disk' },
  { tag: 'snapshot', label: 'Snapshot' },
  { tag: 'export', label: 'Export' },
  { tag: 'display', label: 'Display' },
  { tag: 'entries', label: 'Boot entries', group: 'Manage' },
  { tag: 'settings', label: 'Settings', group: ' ' },
]

export type Nav = (tag: string, prefill?: Record<string, string>) => void

/**
 * Global state: jobs being executed in all processes (command line, TUI, window, other browsers), at most one per menu item (lock group), while different menu items can run at the same time;
 * version is incremented by 1 whenever any job starts / ends, and list pages refresh on it
 */
export type Shell = { running: JobInfo[]; version: number; onJobEnd: () => void; report: (lock: string, now: string) => void }

export default function App() {
  const [page, setPage] = useState(() => location.hash.replace('#', '') || 'overview')
  const [visited, setVisited] = useState<Set<string>>(() => new Set([location.hash.replace('#', '') || 'overview']))
  const [prefill, setPrefill] = useState<{ tag: string; values: Record<string, string>; seq: number } | null>(null)
  const [ops, setOps] = useState<OpInfo[]>([])
  const [error, setError] = useState('')
  const [running, setRunning] = useState<JobInfo[]>([])
  const [version, setVersion] = useState(0)
  const [hash, setHash] = useState('')
  const [tick, setTick] = useState(0)
  const [progress, setProgress] = useState<Record<string, string>>({})   // Where the follow-up job has got to now (lock group → one line)
  const lastChanges = useRef<number | null>(null)
  const [dark, setDark] = useState<boolean | null>(() => {
    try { const v = localStorage.getItem('wlb-theme'); return v === null ? null : v === 'dark' } catch { return null }
  })

  useEffect(() => { api.ops().then(setOps).catch(e => setError(String(e.message ?? e))) }, [])
  useEffect(() => {
    if (dark === null) document.documentElement.removeAttribute('data-theme')
    else document.documentElement.dataset.theme = dark ? 'dark' : 'light'
    try { if (dark === null) localStorage.removeItem('wlb-theme'); else localStorage.setItem('wlb-theme', dark ? 'dark' : 'light') } catch { /* no storage */ }
  }, [dark])
  useEffect(() => { location.hash = page }, [page])

  // Server-pushed state (/api/events): the navigation bar marks menu items being executed and disables "Run" for the same item; version+1 when a job starts / ends makes list pages refresh
  useEffect(() => streamStatus(s => {
    setRunning(s.running)
    setHash(s.hash)
    if (lastChanges.current !== null && s.changes !== lastChanges.current) setVersion(v => v + 1)
    lastChanges.current = s.changes
  }), [])
  // While a job is running, the elapsed time advances every second
  const anyRunning = running.length > 0
  useEffect(() => {
    if (!anyRunning) return
    const h = setInterval(() => setTick(t => t + 1), 1000)
    return () => clearInterval(h)
  }, [anyRunning])

  const report = useCallback((lock: string, now: string) => setProgress(p => (p[lock] ?? '') === now ? p : { ...p, [lock]: now }), [])
  const onJobEnd = useCallback(() => setVersion(v => v + 1), [])
  // Put progress in the tab title, so it is visible even when switched to another tab
  const now = running.map(j => progress[j.lock]).find(Boolean)
  useEffect(() => { document.title = `${now ? now + ' · ' : ''}WinLayerBoot ${hash}` }, [now, hash])

  const go: Nav = (tag, pre) => {
    if (pre) setPrefill({ tag, values: pre, seq: Date.now() })
    setVisited(v => v.has(tag) ? v : new Set(v).add(tag))
    setPage(tag)
  }
  const shell = useMemo<Shell>(() => ({ running, version, onJobEnd, report }), [running, version, onJobEnd, report])

  return (
    <div className="shell">
      <nav className="nav">
        <div className="brand">WinLayerBoot <span className="muted">{hash}</span></div>
        {NAV.map(n => (
          <div key={n.tag}>
            {n.group && <div className="nav-group">{n.group}</div>}
            <button className={page === n.tag ? 'nav-item on' : 'nav-item'} onClick={() => go(n.tag)}>
              <Icon name={n.tag} />{n.label}{running.some(j => j.lock === n.tag) && <span className="dot" title="Running" />}
            </button>
          </div>
        ))}
        {running.map(j => (
          <div key={j.id} className="running-job" onClick={() => go(j.lock === 'entries' ? 'entries' : j.op)} title="Click to view">
            <b>{j.title}</b> <span className="muted">{elapsed(j.t0)}{tick ? '' : ''}</span>
            <progress />
            {progress[j.lock] && <div className="muted small">{progress[j.lock]}</div>}
            {!j.local && <div className="muted small">{j.who}</div>}
          </div>
        ))}
        <div className="nav-foot">
          <button className="link" onClick={() => setDark(dark !== true)}>{dark === true ? 'Light' : 'Dark'}</button>
          <span className="muted">wlb webui</span>
        </div>
      </nav>
      <main className="page">
        {error && <div className="error">{error}</div>}
        {/* Pages stay mounted (switched with hidden), so switching pages does not lose entered content and logs */}
        {[...visited].map(tag => {
          const active = tag === page
          let body
          if (tag === 'overview') body = <Overview go={go} shell={shell} active={active} />
          else if (tag === 'entries') body = <Entries go={go} shell={shell} active={active} />
          else if (tag === 'settings') body = <Settings active={active} />
          else {
            const op = ops.find(o => o.name === tag)
            body = op ? <OpPage op={op} shell={shell} prefill={prefill?.tag === tag ? prefill : null} active={active} /> : <div className="muted">{ops.length ? `No page ${tag}` : 'Connecting to wlb.exe...'}</div>
          }
          return <div key={tag} hidden={!active}>{body}</div>
        })}
      </main>
    </div>
  )
}
