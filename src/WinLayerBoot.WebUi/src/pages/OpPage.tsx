import { useCallback, useEffect, useRef, useState } from 'react'
import { api, downloadPreset, readPreset, streamJob, type CheckResult, type OpInfo } from '../api'
import type { Shell } from '../App'
import Dialog from '../Dialog'
import { FieldForm } from '../Field'
import { applyEvent, finishView, headline, startView, type JobView } from '../job'
import Menu from '../Menu'
import { Bar, CheckPanel, RunPanel } from '../panels'

type Prefill = { values: Record<string, string>; seq: number } | null

/**
 * Operation page. The form is on the left, with an action bar fixed at the bottom; the right side shows check results before running (checked as you edit, no button to press, and problems are also shown under the input boxes) and is replaced by the step bar while running.
 * Output goes over WebSocket. While running, other pages can still be filled in and checked as usual.
 */
export default function OpPage({ op, shell, prefill, active }: { op: OpInfo; shell: Shell; prefill: Prefill; active: boolean }) {
  const [values, setValues] = useState<Record<string, string>>(prefill?.values ?? {})   // Values the user has changed
  const [check, setCheck] = useState<CheckResult | null>(null)
  const [view, setView] = useState<JobView | null>(null)
  const [showRun, setShowRun] = useState(false)
  const [confirm, setConfirm] = useState<CheckResult | null>(null)
  const [vhdDisks, setVhdDisks] = useState<string[] | null>(null)
  const [vhdDisk, setVhdDisk] = useState('')
  const [status, setStatus] = useState('')
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState('')
  const fileRef = useRef<HTMLInputElement>(null)
  const closeRef = useRef<(() => void) | null>(null)
  const attachedRef = useRef<string | null>(null)
  const valuesRef = useRef(values)
  valuesRef.current = values
  const typingRef = useRef(0)
  // Only one check at a time (checks mount disks, so they must not overlap): if changed during a check, check the latest once it finishes
  const flight = useRef<{ running: boolean; next: Record<string, string> | null }>({ running: false, next: null })

  const refresh = useCallback((v: Record<string, string>) => {
    clearTimeout(typingRef.current)
    const f = flight.current
    if (f.running) { f.next = v; return }
    f.running = true
    setBusy(true)
    const go = (cur: Record<string, string>) => {
      api.check(op.name, cur)
        .then(r => { if (!f.next) { setCheck(r); setErr('') } })
        .catch(e => { if (!f.next) setErr((e as Error).message) })
        .finally(() => {
          if (f.next) { const n = f.next; f.next = null; go(n); return }
          f.running = false
          setBusy(false)
        })
    }
    go(v)
  }, [op.name])

  // Re-list every time this page is switched to (base disks and child disks may have changed); values already filled in are left alone
  useEffect(() => { if (active) refresh(valuesRef.current) }, [refresh, active])
  useEffect(() => () => closeRef.current?.(), [])   // Disconnect on leaving
  // Jumped here from another page: with new prefilled values
  useEffect(() => { if (prefill) { setValues(prefill.values); refresh(prefill.values) } }, [prefill?.seq]) // eslint-disable-line react-hooks/exhaustive-deps

  const attach = useCallback((job: Parameters<typeof startView>[0]) => {
    if (attachedRef.current === job.id) return
    attachedRef.current = job.id
    closeRef.current?.()
    setView(startView(job, op.stages))
    setShowRun(true)
    closeRef.current = streamJob(job.id, {
      event: e => setView(v => v && applyEvent(v, e)),
      end: (state, error) => {
        setView(v => v && finishView(v, state, error))
        closeRef.current = null
        attachedRef.current = null
        shell.onJobEnd()
        refresh(valuesRef.current)
      },
    })
  }, [op.stages, refresh, shell])

  // The job running for the same menu item (lock group): whether started by this page, another browser or another frontend
  const lockJob = shell.running.find(j => j.lock === op.lock)
  const running = view?.state === 'running'
  // The running one is exactly this operation: attach to its progress (for one in another process, the server reads its log file and pushes it)
  useEffect(() => { if (lockJob && lockJob.op === op.name && !running) attach(lockJob) }, [lockJob, op.name, running, attach])

  // Report progress to the shell: shown in the navigation bar and tab title
  const now = running && view ? headline(view) : ''
  const report = shell.report
  useEffect(() => { report(op.lock, now) }, [report, op.lock, now])

  const set = (key: string, val: string, rebuild: boolean) => {
    const v = { ...values, [key]: val }
    setValues(v)
    clearTimeout(typingRef.current)
    if (rebuild) refresh(v)
    else typingRef.current = window.setTimeout(() => refresh(valuesRef.current), 400)
  }

  const run = async () => {
    if (lockJob) return
    setErr(''); setBusy(true)
    try {
      // Check again at the moment of the press: the check result may be stale
      const r = await api.check(op.name, values)
      setCheck(r)
      if (!r.passed) return
      if (op.mutates) setConfirm(r)
      else await start(r)
    } catch (e) { setErr((e as Error).message) }
    finally { setBusy(false) }
  }
  const start = async (r: CheckResult) => {
    try {
      attach(await api.run(op.name, r.values))
      shell.onJobEnd()   // Let the navigation bar know right away that a job is running
    } catch (e) { setErr((e as Error).message) }
  }

  const jump = (key: string) => {
    const el = document.querySelector<HTMLElement>(`[data-op="${op.name}"] [data-field="${key}"]`)
    if (!el) return
    const d = el.closest('details')
    if (d) d.open = true
    el.scrollIntoView({ block: 'center', behavior: 'smooth' })
    el.querySelector<HTMLElement>('input, select')?.focus({ preventScroll: true })
  }

  const exportPreset = () => {
    if (!check) return
    const vals = { ...check.values, ...values }
    const p: Record<string, string> = {}
    for (const f of check.fields) if (f.kind !== 'note' && f.kind !== 'password') p[f.key] = vals[f.key] ?? ''
    downloadPreset({ op: op.name, note: `${op.title}, exported by wlb webui`, saved: new Date().toLocaleString(), values: p }, op.presetFile)
    setStatus('Exported preset file (browser download)')
  }
  const importPreset = async (file: File) => {
    try {
      const p = await readPreset(file)
      if (p.op && p.op !== op.name) { setErr(`This is a preset for "${p.op}", not for "${op.name}"`); return }
      setValues(p.values); refresh(p.values); setStatus(`Imported ${file.name}`)
    } catch (e) { setErr((e as Error).message) }
  }
  const pickVhd = async () => {
    try {
      const names = await api.presetDisks()
      if (names.length === 0) { setErr('No child disks available'); return }
      setVhdDisks(names); setVhdDisk(names[0])
    } catch (e) { setErr((e as Error).message) }
  }
  const importFromVhd = async () => {
    setVhdDisks(null)
    try {
      setStatus(`Reading ${vhdDisk}...`)
      const v = await api.presetFromVhd(vhdDisk)
      setValues(v); refresh(v); setStatus(`Imported settings from ${vhdDisk}`)
    } catch (e) { setStatus(''); setErr((e as Error).message) }
  }

  // The same menu item is occupied by another operation (several boot entry management operations share one group)
  const otherRunning = !!lockJob && lockJob.op !== op.name
  const canRun = !!check?.passed && !lockJob && !busy
  return (
    <div className="op" data-op={op.name}>
      <div className="head"><h1>{op.title}</h1><span className="muted">{op.summary}</span></div>
      {err && <Bar tone="fail" title="Something went wrong">{err}</Bar>}
      {otherRunning && <Bar tone="info" title={`'${lockJob.title}' is running on ${lockJob.who}`}>You can fill in settings here, but running must wait for it to finish. Other menu items can run at the same time.</Bar>}
      <div className="split">
        <div className="form">
          {check && (
            <FieldForm fields={check.fields}
                       valueOf={f => values[f.key] ?? check.values[f.key] ?? f.default}
                       problemOf={key => check.checks.find(c => c.field === key && c.state === 'Failed') ?? check.checks.find(c => c.field === key && c.state === 'Warning')}
                       onChange={set}
                       onCommit={key => { if (key in values) refresh(valuesRef.current) }} />
          )}
        </div>
        <aside className="result">
          {showRun && view ? <RunPanel v={view} back={() => setShowRun(false)} />
            : check ? <CheckPanel check={check} op={op} jump={jump} /> : <div className="muted">Checking...</div>}
        </aside>
      </div>
      <div className="actionbar">
        <button type="button" className="accent run" onClick={run} disabled={!canRun} title={lockJob ? `Waiting for '${lockJob.title}' to finish` : check && !check.passed ? 'Fix the problems listed on the right first' : ''}>Run</button>
        <Menu label="Preset" items={[
          { text: 'Import preset...', act: () => fileRef.current?.click() },
          ...(op.name === 'layer' ? [{ text: 'Import from VHD...', act: pickVhd }] : []),
          { text: 'Export preset', act: exportPreset, disabled: !check },
          { text: 'Restore defaults', act: () => { setValues({}); refresh({}) }, separator: true },
        ]} />
        <input ref={fileRef} type="file" accept=".json,application/json" hidden onChange={e => { const f = e.target.files?.[0]; if (f) importPreset(f); e.target.value = '' }} />
        {busy && <span className="spinner" aria-label="Checking" />}
        <span className="status" role="status">{status}</span>
      </div>
      {confirm && (
        <Dialog title={`Start ${op.title}?`} okText="Start" onClose={ok => { const r = confirm; setConfirm(null); if (ok) void start(r) }}>
          <p>{op.summary}</p>
          {op.stages.length > 0 && <ol className="steps muted">{op.stages.map(s => <li key={s}>{s}</li>)}</ol>}
          {confirm.checks.filter(c => c.state === 'Warning').map(c => <Bar key={c.item} tone="warn" title={c.item}>{c.detail}</Bar>)}
        </Dialog>
      )}
      {vhdDisks && (
        <Dialog title="Import from VHD" okText="Import" onClose={ok => { if (ok) void importFromVhd(); else setVhdDisks(null) }}>
          <select value={vhdDisk} onChange={e => setVhdDisk(e.target.value)}>
            {vhdDisks.map(n => <option key={n} value={n}>{n}</option>)}
          </select>
        </Dialog>
      )}
    </div>
  )
}
