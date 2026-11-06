import { useEffect, useRef, useState } from 'react'
import { api, streamJob, type EntryInfo } from '../api'
import type { Nav, Shell } from '../App'
import Dialog from '../Dialog'
import { Bar } from '../panels'
import { useLoad } from '../useLoad'

// The same set of operations as the boot entries pages of the window and terminal (EntryAction in WinLayerBoot.Core/Views.cs): page jumps to a form, rename pops up a dialog, confirm asks for confirmation, the rest act directly on click
type Action = { text: string; op: string; kind: 'page' | 'rename' | 'confirm' | 'instant'; needSel: boolean; wlbOnly: boolean }
const ACTIONS: Action[] = [
  { text: 'Add from VHD', op: 'entry-add', kind: 'page', needSel: false, wlbOnly: false },
  { text: 'Rename', op: 'entry-rename', kind: 'rename', needSel: true, wlbOnly: true },
  { text: 'Delete', op: 'entry-delete', kind: 'confirm', needSel: true, wlbOnly: true },
  { text: 'Hide', op: 'entry-hide', kind: 'instant', needSel: true, wlbOnly: true },
  { text: 'Show', op: 'entry-show', kind: 'instant', needSel: true, wlbOnly: true },
  { text: 'Set as next boot', op: 'entry-next', kind: 'instant', needSel: true, wlbOnly: false },
  { text: 'Set as default', op: 'entry-default', kind: 'instant', needSel: true, wlbOnly: false },
]

function applies(a: Action, e: EntryInfo | undefined) {
  if (!a.needSel) return true
  if (!e || (a.wlbOnly && !e.isWlb)) return false
  if (a.op === 'entry-hide') return !e.hidden
  if (a.op === 'entry-show') return e.hidden
  if (a.op === 'entry-default') return !e.isDefault
  return true
}

function Item({ e, on, pick }: { e: EntryInfo; on: boolean; pick: () => void }) {
  return (
    <button type="button" role="radio" aria-checked={on} className={on ? 'item on' : 'item'} onClick={pick}>
      <span className="item-title">
        <b>{e.desc}</b>
        {e.flags.map(f => <span key={f} className={f === 'File missing' ? 'pill bad' : f === 'Default' || f === 'Next boot' ? 'pill on' : 'pill'}>{f}</span>)}
      </span>
      <span className="muted small"><code>{e.id}</code> → {e.vhd}</span>
    </button>
  )
}

export default function Entries({ go, shell, active }: { go: Nav; shell: Shell; active: boolean }) {
  const { data, err, busy, load } = useLoad(api.entries)
  const [sel, setSel] = useState('')
  const [result, setResult] = useState<{ tone: 'ok' | 'fail'; title: string; text: string } | null>(null)
  const [renaming, setRenaming] = useState('')
  const [deleting, setDeleting] = useState(false)
  const [working, setWorking] = useState(false)
  const closeRef = useRef<(() => void) | null>(null)
  useEffect(() => { if (active) load() }, [active, shell.version, load])
  useEffect(() => () => closeRef.current?.(), [])
  const list = data ?? []
  const wlb = list.filter(e => e.isWlb)
  const sys = list.filter(e => e.tier === 'system')
  const other = list.filter(e => e.tier === 'other')
  const cur = list.find(e => e.id === sel && e.tier !== 'other')

  const execute = async (a: Action, values: Record<string, string>) => {
    setResult(null); setWorking(true)
    try {
      const r = await api.check(a.op, values)
      if (!r.passed) {
        setResult({ tone: 'fail', title: `Cannot perform '${a.text}'`, text: r.checks.filter(c => c.state === 'Failed').map(c => `${c.item}: ${c.detail}`).join('\n') })
        setWorking(false)
        return
      }
      const job = await api.run(a.op, r.values)
      closeRef.current = streamJob(job.id, {
        event: () => { /* Fast, so the progress is not shown */ },
        end: (state, error) => {
          setResult(state === 'done' ? { tone: 'ok', title: `${job.title}: Done`, text: '' } : { tone: 'fail', title: `${job.title}: Failed`, text: error })
          setWorking(false)
          shell.onJobEnd()
        },
      })
    } catch (e) {
      setResult({ tone: 'fail', title: `Cannot perform '${a.text}'`, text: (e as Error).message })
      setWorking(false)
    }
  }

  const act = (a: Action) => {
    if (a.kind === 'page') go(a.op, cur && a.needSel ? { Entry: cur.desc } : undefined)
    else if (a.kind === 'rename') setRenaming(cur?.desc ?? '')
    else if (a.kind === 'confirm') setDeleting(true)
    else if (cur) void execute(a, { Entry: cur.desc })
  }
  const named = (op: string) => ACTIONS.find(a => a.op === op)!

  return (
    <>
      <div className="head"><h1>Boot entries</h1><button type="button" onClick={load}>Refresh</button>{(busy || working) && <span className="spinner" aria-label="Processing" />}</div>
      {err && <Bar tone="fail" title="Failed to load">{err}</Bar>}
      {result && <Bar tone={result.tone} title={result.title}>{result.text}</Bar>}
      <div className="toolbar" role="toolbar">
        {ACTIONS.map(a => (
          <button type="button" key={a.op} className={a.needSel ? '' : 'accent'} disabled={!applies(a, cur) || working} onClick={() => act(a)}>{a.text}</button>
        ))}
      </div>
      <h3>Boot entries starting with WLB-</h3>
      {data && wlb.length === 0 && <div className="muted">None</div>}
      <div className="list" role="radiogroup">{wlb.map(e => <Item key={e.id} e={e} on={e.id === sel} pick={() => setSel(e.id)} />)}</div>
      {sys.length > 0 && (
        <>
          <h3>Non-VHD system boot entries <span className="muted small">Can only be set as default or next boot</span></h3>
          <div className="list" role="radiogroup">{sys.map(e => <Item key={e.id} e={e} on={e.id === sel} pick={() => setSel(e.id)} />)}</div>
        </>
      )}
      {other.length > 0 && (
        <details className="group">
          <summary>Other VHD boot entries: {other.length} (not managed, listed only)</summary>
          <div className="group-body fine">{other.map(e => <div key={e.id} className="muted">{e.desc} {e.vhd} {e.flags.join(', ')}</div>)}</div>
        </details>
      )}
      {renaming && cur && (
        <Dialog title="Rename boot entry" okText="Rename" onClose={ok => { const d = renaming; setRenaming(''); if (ok && d !== cur.desc) void execute(named('entry-rename'), { Entry: cur.desc, Desc: d }) }}>
          <label className="field"><span>New name</span><input autoFocus value={renaming} onChange={e => setRenaming(e.target.value)} /></label>
        </Dialog>
      )}
      {deleting && cur && (
        <Dialog title="Delete boot entry" okText="Delete" danger onClose={ok => { setDeleting(false); if (ok) void execute(named('entry-delete'), { Entry: cur.desc }) }}>
          Delete '{cur.desc}' from the boot menu? The VHDX file is not touched; you can add it back later with 'Add from VHD'.
        </Dialog>
      )}
    </>
  )
}
