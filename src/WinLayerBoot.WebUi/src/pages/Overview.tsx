import { useEffect } from 'react'
import { api, type TreeNode } from '../api'
import type { Nav, Shell } from '../App'
import { Bar } from '../panels'
import { useLoad } from '../useLoad'

/** What can be done with a disk: jump to the matching operation, prefilled with this disk (same as DiskAction in WinLayerBoot.Core/Views.cs). */
function actions(n: TreeNode): { text: string; op: string; pre: Record<string, string> }[] {
  if (n.broken) return []
  if (n.kind === 'base') return [{ text: 'Create child disk from this', op: 'layer', pre: { Base: n.name } }]
  const wlb = n.entries[0]
  return [
    { text: 'Take snapshot', op: 'snapshot', pre: { Parent: n.name } },
    wlb ? { text: 'Export as standalone disk', op: 'export', pre: { Entry: wlb.desc } } : { text: 'Add boot entry', op: 'entry-add', pre: { Vhd: n.name } },
    { text: 'Save display settings', op: 'display', pre: { Source: n.name } },
  ]
}

function Node({ n, go }: { n: TreeNode; go: Nav }) {
  return (
    <li>
      <div className={n.broken ? 'card disk broken' : 'card disk'}>
        <div className="disk-main">
          <b>{n.name}.vhdx</b>
          <div className="pills">{n.tags.map(t => <span key={t} className="pill">{t}</span>)}</div>
          {n.broken && <div className="st-Failed">{n.link}</div>}
          {n.entries.map(e => <div key={e.id} className="muted small">Boot entry: {e.desc}{e.flags.length > 0 && ` (${e.flags.join(', ')})`}</div>)}
          {n.entries.length === 0 && n.kind !== 'base' && <div className="muted small">No boot entries</div>}
        </div>
        <div className="disk-actions">
          {actions(n).map(a => <button type="button" key={a.op} onClick={() => go(a.op, a.pre)}>{a.text}</button>)}
        </div>
      </div>
      {n.children.length > 0 && <ul className="tree">{n.children.map(c => <Node key={c.path} n={c} go={go} />)}</ul>}
    </li>
  )
}

function count(ns: TreeNode[], f: (n: TreeNode) => number): number {
  return ns.reduce((sum, n) => sum + f(n) + count(n.children, f), 0)
}

export default function Overview({ go, shell, active }: { go: Nav; shell: Shell; active: boolean }) {
  const { data, err, busy, load } = useLoad(api.overview)
  // Refresh the first time, every time it is switched back to, and when a job starts / ends
  useEffect(() => { if (active) load() }, [active, shell.version, load])
  const broken = data ? count(data.roots, n => n.broken ? 1 : 0) : 0
  return (
    <>
      <div className="head"><h1>Overview</h1><button type="button" onClick={load}>Refresh</button>{busy && <span className="spinner" aria-label="Loading" />}</div>
      {err && <Bar tone="fail" title="Failed to load">{err}</Bar>}
      {data && (
        <>
          {!data.isAdmin && <Bar tone="fail" title="Not administrator">You can view, but operations will fail. Restart wlb webui as administrator.</Bar>}
          {!data.onWorkHost && <Bar tone="warn" title="Not on the work host">Creating child disks, snapshots and exports, and adding boot entries must run on the work host.</Bar>}
          {broken > 0 && <Bar tone="warn" title={`${broken} disk(s) with problems`}>See the red cards below.</Bar>}
          <div className="stats">
            <div className="card stat"><b>{count(data.roots, () => 1)}</b><span className="muted">Disks</span></div>
            <div className="card stat"><b>{count(data.roots, n => n.kind === 'base' ? 1 : 0)}</b><span className="muted">Base disks</span></div>
            <div className="card stat"><b>{count(data.roots, n => n.entries.length)}</b><span className="muted">WLB boot entries</span></div>
            <div className="card stat"><b>{data.freeGB} GB</b><span className="muted">{data.vhdDir} free</span></div>
          </div>
          {data.roots.length === 0 && (
            <div className="card empty">
              <b>No VHDX in the folder yet</b>
              <p className="muted">The first step is to create a base disk from the install ISO.</p>
              <button type="button" className="accent" onClick={() => go('base')}>Create a base disk</button>
            </div>
          )}
          <ul className="tree root">{data.roots.map(r => <Node key={r.path} n={r} go={go} />)}</ul>
          {data.orphans.length > 0 && (
            <details className="group">
              <summary>{data.orphans.length} boot entries point to missing files or files outside the folder</summary>
              <div className="group-body fine">{data.orphans.map(e => <div key={e.id} className="muted">{e.desc} → {e.vhd}</div>)}</div>
            </details>
          )}
        </>
      )}
    </>
  )
}
