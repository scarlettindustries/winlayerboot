import { useEffect, useState } from 'react'
import { api, type ConfigInfo } from '../api'
import { FieldForm } from '../Field'
import { Bar } from '../panels'

/** Settings: drawn from the field table given by the server (the same control as the operation pages), the boot menu language is a dropdown; only changed items are submitted. */
export default function Settings({ active }: { active: boolean }) {
  const [cfg, setCfg] = useState<ConfigInfo | null>(null)
  const [vals, setVals] = useState<Record<string, string>>({})   // Changed, not yet saved
  const [saved, setSaved] = useState('')
  const [err, setErr] = useState('')
  const load = () => api.config().then(c => { setErr(''); setCfg(c); setVals({}); setSaved('') }).catch(e => setErr(e.message))
  // Files in the iso directory may have changed; if there are unsaved changes, leave them alone
  useEffect(() => { if (active && Object.keys(vals).length === 0) load() }, [active]) // eslint-disable-line react-hooks/exhaustive-deps
  const save = async () => {
    setErr('')
    try { const c = await api.saveConfig(vals); setCfg(c); setVals({}); setSaved(`Saved to ${c.path}`) }
    catch (e) { setErr((e as Error).message) }
  }
  const dirty = Object.keys(vals).length > 0
  return (
    <div className="op">
      <div className="head"><h1>Settings</h1><span className="muted">Shared settings for all four interfaces{cfg ? `: ${cfg.path}` : ''}</span></div>
      {err && <Bar tone="fail" title="Something went wrong">{err}</Bar>}
      {saved && <Bar tone="ok" title={saved} />}
      {cfg && (
        <div className="form narrow">
          <FieldForm fields={cfg.fields} valueOf={f => vals[f.key] ?? f.default}
                     onChange={(k, v) => { setVals({ ...vals, [k]: v }); setSaved('') }} />
          <div className="card">
            <b>ISOs in use</b>
            <dl className="results plain">
              <div><dt>LOF</dt><dd>{cfg.resolved.LofIso || '(not found)'}</dd></div>
              <div><dt>virtio</dt><dd>{cfg.resolved.VirtioIso || '(not found)'}</dd></div>
            </dl>
          </div>
        </div>
      )}
      {cfg && (
        <div className="actionbar">
          <button type="button" className="accent run" onClick={save} disabled={!dirty}>Save</button>
          <button type="button" onClick={load} disabled={!dirty}>Discard changes</button>
          <span className="status">{dirty ? 'Unsaved changes' : ''}</span>
        </div>
      )}
    </div>
  )
}
