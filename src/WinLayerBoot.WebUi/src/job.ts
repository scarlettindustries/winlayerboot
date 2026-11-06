import type { Event, JobInfo } from './api'

// How one run looks on the page (the same set as WinLayerBoot.Core/JobView.cs): feed in the job's events to get the step bar, progress, result and notices

export type StepState = 'pending' | 'running' | 'done' | 'skipped' | 'failed'
export type Step = { label: string; detail: string; state: StepState }
export type JobView = {
  job: JobInfo
  steps: Step[]
  percent: number        // Percentage of the current step, -1 if none
  activity: string       // What is being done now: the latest output line
  results: [string, string][]
  warnings: string[]
  log: string[]
  state: string          // running / done / failed
  error: string
  endedAt: number | null
}

export function startView(job: JobInfo, stages: string[]): JobView {
  return { job, steps: stages.map(label => ({ label, detail: '', state: 'pending' })), percent: -1, activity: '', results: [], warnings: [], log: [], state: 'running', error: '', endedAt: null }
}

function enter(v: JobView, no: number, detail: string, skipped: boolean): JobView {
  if (no < 1 || no > v.steps.length) return { ...v, percent: -1, activity: '' }
  const steps = v.steps.map((s, i): Step =>
    i < no - 1 && (s.state === 'running' || s.state === 'pending') ? { ...s, state: 'done' }
      : i === no - 1 ? { ...s, detail, state: skipped ? 'skipped' : 'running' } : s)
  return { ...v, steps, percent: -1, activity: '' }
}

function line(v: JobView, text: string, level: string): JobView {
  const log = [...v.log, text]
  const t = text.trim()
  if (t === '' || level === 'title') return { ...v, log }
  return {
    ...v, log,
    warnings: level === 'warn' ? [...v.warnings, t] : v.warnings,
    activity: level === 'info' || level === 'ok' ? t : v.activity,
  }
}

const STAGE_LINE = /^==== (\d+)\/\d+ [^:]+(?::\s*(.*))?$/

export function applyEvent(v: JobView, e: Event): JobView {
  switch (e.kind) {
    case 'stage': return enter(v, e.no, e.detail, e.skipped)
    case 'progress': {
      // "Unpacking image 42%": the percentage is shown separately; no need to repeat it if it has the same name as the step
      const what = e.text.replace(/\s*\d+%\s*$/, '').trim()
      const cur = v.steps.find(s => s.state === 'running')
      return { ...v, percent: e.percent, activity: cur?.label.startsWith(what) ? '' : what }
    }
    case 'result': return { ...v, results: [...v.results, [e.text, e.detail]] }
    case 'checks': return v
    default: {
      // Job in another process: the server pushes lines of the log file (level is always info), and steps are reconstructed from the format of the title line
      const m = e.level === 'info' ? STAGE_LINE.exec(e.text) : null
      if (m) return { ...enter(v, Number(m[1]), m[2] ?? '', false), log: [...v.log, e.text] }
      return line(v, e.text, e.level)
    }
  }
}

export function finishView(v: JobView, state: string, error: string): JobView {
  const steps = v.steps.map((s): Step => s.state === 'running' ? { ...s, state: state === 'done' ? 'done' : 'failed' } : s)
  return { ...v, steps, state, error, percent: -1, endedAt: Date.now() }
}

/** One-line progress (used in the tab title and navigation bar). */
export function headline(v: JobView): string {
  const i = v.steps.findIndex(s => s.state === 'running')
  if (i < 0) return v.activity
  return `Step ${i + 1}/${v.steps.length}: ${v.steps[i].label}${v.percent >= 0 ? ` ${v.percent}%` : ''}`
}

/** Elapsed time from t0 to end (default now). */
export function elapsed(t0: number, end: number = Date.now()) {
  const d = Math.max(0, Math.round((end - t0) / 1000))
  return `${Math.floor(d / 60)}:${String(d % 60).padStart(2, '0')}`
}
