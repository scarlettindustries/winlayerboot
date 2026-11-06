import { useEffect, useRef, useState } from 'react'

export type MenuItem = { text: string; act: () => void; disabled?: boolean; separator?: boolean }

/** Dropdown menu: closes on click outside or Esc. */
export default function Menu({ label, items, disabled }: { label: string; items: MenuItem[]; disabled?: boolean }) {
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!open) return
    const down = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false) }
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    document.addEventListener('mousedown', down)
    document.addEventListener('keydown', key)
    return () => { document.removeEventListener('mousedown', down); document.removeEventListener('keydown', key) }
  }, [open])
  return (
    <div className="menu" ref={ref}>
      <button type="button" aria-haspopup="menu" aria-expanded={open} disabled={disabled} onClick={() => setOpen(!open)}>{label} ▾</button>
      {open && (
        <div className="menu-list" role="menu">
          {items.map(i => (
            <button type="button" key={i.text} role="menuitem" disabled={i.disabled} className={i.separator ? 'sep' : ''} onClick={() => { setOpen(false); i.act() }}>{i.text}</button>
          ))}
        </div>
      )}
    </div>
  )
}
