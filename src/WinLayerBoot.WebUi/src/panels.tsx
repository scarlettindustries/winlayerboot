import { useEffect, useState } from 'react'
import type { CheckResult, OpInfo } from './api'
import { elapsed, type JobView, type Step } from './job'

const ICON: Record<string, string> = { Failed: '✕', Warning: '!', OK: '✓', Skipped: '–' }

/** A one-line conclusion, colored like WinUI's InfoBar. */
export function Bar({ tone, title, children }: { tone: 'ok' | 'warn' | 'fail' | 'info'; title: string; children?: React.ReactNode }) {
  return (
    <div className={'bar ' + tone} role="status">
      <b>{title}</b>
      {children && <div className="bar-body">{children}</div>}
    </div>
  )
}

/** Status before execution: conclusion, problems to handle (click to jump to the matching input box), passed items collapsed, accompanying tables, what is about to be done. */
export function CheckPanel({ check, op, jump }: { check: CheckResult; op: OpInfo; jump: (key: string) => void }) {
  const fails = check.checks.filter(c => c.state === 'Failed')
  const warns = check.checks.filter(c => c.state === 'Warning')
  const fine = check.checks.filter(c => c.state === 'OK' || c.state === 'Skipped')
  return (
    <>
      {fails.length > 0 ? <Bar tone="fail" title={`${fails.length} problem(s) to fix`} />
        : warns.length > 0 ? <Bar tone="warn" title={`Ready to run, ${warns.length} warning(s)`} />
          : <Bar tone="ok" title="Ready to run" />}
      {[...fails, ...warns].map(c => {
        const body = <><span className={'icon st-' + c.state}>{ICON[c.state]}</span><span><b>{c.item}</b><br /><span className="muted">{c.detail}</span></span></>
        return c.field
          ? <button key={c.item} type="button" className="problem-row" title="Go to this field" onClick={() => jump(c.field)}>{body}</button>
          : <div key={c.item} className="problem-row">{body}</div>
      })}
      {fine.length > 0 && (
        <details className="group">
          <summary>{fine.length} passed</summary>
          <div className="group-body fine">
            {fine.map(c => <div key={c.item}><span className={'icon st-' + c.state}>{ICON[c.state]}</span> {c.item}{c.detail && <span className="muted">: {c.detail}</span>}</div>)}
          </div>
        </details>
      )}
      {check.extraHeaders.length > 0 && (
        <div className="card scroll-x">
          <table className="data">
            <thead><tr>{check.extraHeaders.map(h => <th key={h}>{h}</th>)}</tr></thead>
            <tbody>{check.extraRows.map((r, i) => <tr key={i}>{r.map((c, j) => <td key={j}>{c}</td>)}</tr>)}</tbody>
          </table>
        </div>
      )}
      {op.steps.length > 0 && (
        <div className="card">
          <b>What will happen</b>
          <ol className="steps muted">{op.steps.map((s, i) => <li key={i}>{s}</li>)}</ol>
        </div>
      )}
    </>
  )
}

function copyText(text: string) {
  // navigator.clipboard is unavailable when neither https nor localhost
  if (navigator.clipboard) { void navigator.clipboard.writeText(text); return }
  const t = document.createElement('textarea')
  t.value = text
  document.body.append(t)
  t.select()
  document.execCommand('copy')
  t.remove()
}

function StepRow({ s, v }: { s: Step; v: JobView }) {
  const cur = s.state === 'running'
  return (
    <li className={'step ' + s.state} aria-current={cur ? 'step' : undefined}>
      <span className="step-icon">{cur ? <span className="spinner" /> : s.state === 'done' ? '✓' : s.state === 'failed' ? '✕' : s.state === 'skipped' ? '–' : ''}</span>
      <div className="step-body">
        <div className="step-label">{s.label}{cur && v.percent >= 0 ? <span className="muted"> {v.percent}%</span> : null}</div>
        {s.detail && <div className="step-detail">{s.detail}</div>}
        {cur && (v.percent >= 0 ? <progress value={v.percent} max={100} /> : <progress />)}
        {cur && v.activity && <div className="step-detail">{v.activity}</div>}
      </div>
    </li>
  )
}

/** Status during and after execution: conclusion, step bar (current step with a progress bar), notices, result, expandable detailed log. */
export function RunPanel({ v, back }: { v: JobView; back: () => void }) {
  const running = v.state === 'running'
  const [, tick] = useState(0)
  useEffect(() => {
    if (!running) return
    const h = setInterval(() => tick(t => t + 1), 1000)
    return () => clearInterval(h)
  }, [running])
  const took = elapsed(v.job.t0, v.endedAt ?? Date.now())
  return (
    <>
      {running ? <Bar tone="info" title={`Running${v.job.local ? '' : ` (${v.job.who})`}`}>Elapsed {took}</Bar>
        : v.state === 'done' ? <Bar tone="ok" title={`${v.job.title} done`}>Took {took}</Bar>
          : <Bar tone="fail" title={`${v.job.title} failed`}>{v.error}</Bar>}
      {v.steps.length > 0
        ? <ol className="card stepper">{v.steps.map(s => <StepRow key={s.label} s={s} v={v} />)}</ol>
        : running && <div className="card"><progress />{v.activity && <div className="step-detail">{v.activity}</div>}</div>}
      {v.warnings.map((w, i) => <Bar key={i} tone="warn" title={w} />)}
      {v.results.length > 0 && (
        <dl className="card results">
          {v.results.map(([k, val]) => (
            <div key={k}><dt>{k}</dt><dd>{val}</dd><button type="button" className="copy" title="Copy" onClick={() => copyText(val)}>Copy</button></div>
          ))}
        </dl>
      )}
      <details className="group">
        <summary>Detailed log</summary>
        <pre className="log">{v.log.join('\n')}</pre>
      </details>
      {!running && <div className="buttons"><button type="button" onClick={back}>Back to checks</button></div>}
    </>
  )
}
