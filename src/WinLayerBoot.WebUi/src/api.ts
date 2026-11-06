// Corresponds to the /api endpoints of wlb.exe (WinLayerBoot.Core/Api.cs, WinLayerBoot.Cli/Web/WebServer.cs)

export type FieldInfo = {
  key: string; label: string; kind: 'text' | 'bool' | 'int' | 'choice' | 'path' | 'password' | 'note'
  default: string; help: string; group: string; choices: string[]; strict: boolean; advanced: boolean
}
export type OpInfo = { name: string; presetFile: string; title: string; summary: string; steps: string[]; stages: string[]; mutates: boolean; category: string; lock: string }
export type CheckInfo = { item: string; state: string; detail: string; field: string }
export type CheckResult = { values: Record<string, string>; fields: FieldInfo[]; checks: CheckInfo[]; extraHeaders: string[]; extraRows: string[][]; passed: boolean; warns: number }
/** kind: line / progress (percent, -1 if absent) / stage (no, total; text is the step name, detail, skipped) / result (text is the key, detail is the value) / checks / state */
export type Event = { seq: number; kind: string; text: string; level: string; no: number; total: number; detail: string; skipped: boolean; percent: number; checks: CheckInfo[] }
/** t0: start time (local clock, computed from elapsedMs on receipt, unaffected by time zones on either side); lock: lock group (menu item), only one can run at a time per group; who / pid / local: who runs it and in which process, local = in this webui itself */
export type JobInfo = {
  id: string; op: string; title: string; state: string; error: string; started: string; elapsedMs: number; t0: number; logPath: string; changes: number
  lock: string; who: string; pid: number; local: boolean; events: Event[]
}
/** Jobs being executed in all processes (command line, TUI, window, Web), at most one per lock group */
export type StatusInfo = { changes: number; running: JobInfo[]; hash: string }
export type EntryInfo = { id: string; desc: string; vhd: string; vhdExists: boolean; hidden: boolean; isDefault: boolean; isBootNext: boolean; ems: boolean; isWlb: boolean; isVhd: boolean; tier: 'wlb' | 'system' | 'other'; flags: string[] }
export type TreeNode = {
  name: string; path: string; kind: string; readOnly: boolean; attached: boolean; dirtyLog: boolean; fileGB: number; maxGB: number
  link: string; broken: boolean; mtime: string; tags: string[]; entries: EntryInfo[]; children: TreeNode[]
}
export type Overview = { roots: TreeNode[]; orphans: EntryInfo[]; otherEntries: EntryInfo[]; vhdDir: string; freeGB: number; host: string; onWorkHost: boolean; isAdmin: boolean; env: string; totals: string }
export type ConfigInfo = { values: Record<string, string>; fields: FieldInfo[]; isos: string[]; path: string; resolved: Record<string, string> }
export type Preset = { op: string; note: string; saved: string; values: Record<string, string> }

async function call<T>(method: string, url: string, body?: unknown): Promise<T> {
  const r = await fetch(url, { method, headers: body === undefined ? {} : { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) })
  const text = await r.text()
  let data: unknown = null
  try { data = text ? JSON.parse(text) : null } catch { /* not JSON */ }
  if (!r.ok) {
    const msg = (data as { message?: string } | null)?.message ?? `${r.status} ${r.statusText}`
    throw new Error(msg)
  }
  return data as T
}

const stamp = (j: JobInfo): JobInfo => ({ ...j, t0: Date.now() - j.elapsedMs })

export const api = {
  overview: () => call<Overview>('GET', '/api/overview'),
  ops: () => call<OpInfo[]>('GET', '/api/ops'),
  check: (op: string, values: Record<string, string>) => call<CheckResult>('POST', `/api/ops/${op}/check`, { values }),
  run: (op: string, values: Record<string, string>) => call<JobInfo>('POST', `/api/ops/${op}/run`, { values }).then(stamp),
  job: (id: string, after: number) => call<JobInfo>('GET', `/api/jobs/${id}/events?after=${after}`),
  entries: () => call<EntryInfo[]>('GET', '/api/entries'),
  status: () => call<StatusInfo>('GET', '/api/status'),
  config: () => call<ConfigInfo>('GET', '/api/config'),
  saveConfig: (values: Record<string, string>) => call<ConfigInfo>('PUT', '/api/config', { values }),
  presetDisks: () => call<string[]>('GET', '/api/preset/disks'),
  presetFromVhd: (disk: string) => call<Record<string, string>>('GET', `/api/preset/from-vhd?disk=${encodeURIComponent(disk)}`),
}

/**
 * Job output goes over WebSocket: /api/jobs/<id>/ws. The server first sends the existing output, then pushes each new item as it is produced,
 * and at the end sends kind=state (text=done/failed, level=error message) and closes. Returns a function that closes it.
 */
export function streamJob(id: string, on: { event: (e: Event) => void; end: (state: string, error: string) => void }): () => void {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws'
  const ws = new WebSocket(`${proto}://${location.host}/api/jobs/${id}/ws`)
  let ended = false
  const finish = async (fallback: string) => {
    if (ended) return
    ended = true
    // No end message received (connection dropped unexpectedly): query the job status once
    try { const j = await api.job(id, Number.MAX_SAFE_INTEGER); on.end(j.state === 'running' ? fallback : j.state, j.error) }
    catch { on.end(fallback, '') }
  }
  ws.onmessage = e => {
    const ev = JSON.parse(e.data) as Event
    if (ev.kind === 'state') { ended = true; on.end(ev.text, ev.level); ws.close(); return }
    on.event(ev)
  }
  ws.onerror = () => finish('failed')
  ws.onclose = () => finish('failed')
  return () => { ended = true; ws.close() }
}

/**
 * State push: /api/events (WebSocket). On connect it first receives the current state once, then once whenever a job in any process starts / ends (changes is a cumulative count), with a 20-second heartbeat.
 * Reconnects 3 seconds after a drop. Returns a function that closes it.
 */
export function streamStatus(on: (s: StatusInfo) => void): () => void {
  let ws: WebSocket | null = null
  let closed = false
  let timer = 0
  const open = () => {
    const proto = location.protocol === 'https:' ? 'wss' : 'ws'
    ws = new WebSocket(`${proto}://${location.host}/api/events`)
    ws.onmessage = e => { const st = JSON.parse(e.data) as StatusInfo; on({ ...st, running: st.running.map(stamp) }) }
    ws.onclose = () => { if (!closed) timer = window.setTimeout(open, 3000) }
  }
  open()
  return () => { closed = true; clearTimeout(timer); ws?.close() }
}

/** Preset file: the same JSON as the CLI's --save/--load and the import/export of the TUI and WinUI */
export function downloadPreset(p: Preset, file: string) {
  const blob = new Blob([JSON.stringify(p, null, 2)], { type: 'application/json' })
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob)
  a.download = file
  a.click()
  setTimeout(() => URL.revokeObjectURL(a.href), 1000)
}

export function readPreset(file: File): Promise<Preset> {
  return new Promise((resolve, reject) => {
    const r = new FileReader()
    r.onload = () => {
      try {
        const p = JSON.parse(String(r.result)) as Preset
        if (!p || typeof p !== 'object' || !p.values) throw new Error('Not a preset file')
        resolve(p)
      } catch (e) { reject(e instanceof Error ? e : new Error(String(e))) }
    }
    r.onerror = () => reject(new Error('Failed to read file'))
    r.readAsText(file)
  })
}
