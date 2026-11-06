import type { CheckInfo, FieldInfo } from './api'

const yes = (v: string) => /^(y|yes|true|1)$/i.test(v)

/** Shows problems found by checks under the input box. */
function Problem({ p }: { p?: CheckInfo }) {
  if (!p) return null
  return <small className={p.state === 'Failed' ? 'problem fail' : 'problem warn'} role="alert">{p.item}: {p.detail}</small>
}

/**
 * Input control for one preset item, shared by operation pages and the settings page (drawn from the same field table as the window and terminal).
 * onChange(value, whether to recompute the field table now): switches, dropdown selections and picking from candidates recompute immediately, while the page waits a moment while typing before recomputing;
 * onCommit: called when the input box loses focus (or on Enter).
 */
export default function Field({ f, value, problem, onChange, onCommit }: {
  f: FieldInfo
  value: string
  problem?: CheckInfo
  onChange: (v: string, rebuild: boolean) => void
  onCommit?: () => void
}) {
  if (f.kind === 'note') return <div className="note"><span>{f.label}</span><span className="muted">{f.default}</span></div>
  const help = f.help && <small>{f.help}</small>
  const invalid = problem?.state === 'Failed'
  if (f.kind === 'bool') return (
    <label className="field switch" data-field={f.key}>
      <span className="switch-text"><span>{f.label}</span>{help}</span>
      <span className="sw"><input type="checkbox" role="switch" checked={yes(value)} onChange={e => onChange(e.target.checked ? 'Y' : 'N', true)} /><i /></span>
      <Problem p={problem} />
    </label>
  )
  if (f.kind === 'choice' && f.strict) return (
    <label className="field" data-field={f.key}>
      <span>{f.label}</span>
      <select value={value} aria-invalid={invalid} onChange={e => onChange(e.target.value, true)}>
        {!f.choices.includes(value) && <option value={value}>{value}</option>}
        {f.choices.map(c => <option key={c} value={c}>{c}</option>)}
      </select>
      {help}
      <Problem p={problem} />
    </label>
  )
  if (f.kind === 'choice') return (
    <label className="field" data-field={f.key}>
      <span>{f.label}</span>
      <input list={'dl-' + f.key} value={value} aria-invalid={invalid} onChange={e => onChange(e.target.value, f.choices.includes(e.target.value))} onBlur={onCommit} />
      <datalist id={'dl-' + f.key}>{f.choices.map(c => <option key={c} value={c} />)}</datalist>
      {help}
      <Problem p={problem} />
    </label>
  )
  return (
    <label className="field" data-field={f.key}>
      <span>{f.label}</span>
      <input type={f.kind === 'password' ? 'password' : f.kind === 'int' ? 'number' : 'text'} value={value} aria-invalid={invalid}
             onChange={e => onChange(e.target.value, false)} onBlur={onCommit}
             onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }} />
      {help}
      <Problem p={problem} />
    </label>
  )
}

/** The whole form: consecutive fields of the same group go into one collapsible card (native details), and ungrouped ones are plain cards. */
export function FieldForm({ fields, valueOf, problemOf, onChange, onCommit }: {
  fields: FieldInfo[]
  valueOf: (f: FieldInfo) => string
  problemOf?: (key: string) => CheckInfo | undefined
  onChange: (key: string, v: string, rebuild: boolean) => void
  onCommit?: (key: string) => void
}) {
  const groups: { name: string; fields: FieldInfo[] }[] = []
  for (const f of fields) {
    const last = groups[groups.length - 1]
    if (last && last.name === f.group) last.fields.push(f)
    else groups.push({ name: f.group, fields: [f] })
  }
  const body = (g: { fields: FieldInfo[] }) => g.fields.map(f => (
    <Field key={f.key} f={f} value={valueOf(f)} problem={problemOf?.(f.key)}
           onChange={(v, rebuild) => onChange(f.key, v, rebuild)} onCommit={() => onCommit?.(f.key)} />
  ))
  return (
    <>
      {groups.map(g => g.name
        ? <details key={g.name} className="group" open><summary>{g.name}</summary><div className="group-body">{body(g)}</div></details>
        : <div key="-" className="group plain"><div className="group-body">{body(g)}</div></div>)}
    </>
  )
}
