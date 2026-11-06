import { useEffect, useRef, type ReactNode } from 'react'

/** Confirmation dialog on native <dialog>: Esc and "Cancel" both count as cancel. danger: the OK button is a dangerous operation, and default focus goes on Cancel. */
export default function Dialog({ title, okText, cancelText = 'Cancel', danger, onClose, children }: {
  title: string; okText: string; cancelText?: string; danger?: boolean; onClose: (ok: boolean) => void; children: ReactNode
}) {
  const ref = useRef<HTMLDialogElement>(null)
  const cancel = useRef<HTMLButtonElement>(null)
  useEffect(() => {
    const d = ref.current
    if (!d || d.open) return
    d.showModal()
    // showModal puts focus on the first control (React's autoFocus does not count): for dangerous operations put it on Cancel, so Enter cannot delete by mistake
    if (danger) cancel.current?.focus()
  }, [danger])
  return (
    <dialog ref={ref} className="dialog" onCancel={e => { e.preventDefault(); onClose(false) }}>
      <form method="dialog" onSubmit={e => { e.preventDefault(); onClose(true) }}>
        <h2>{title}</h2>
        <div className="dialog-body">{children}</div>
        <div className="dialog-buttons">
          <button type="submit" className={danger ? 'danger' : 'accent'}>{okText}</button>
          <button type="button" ref={cancel} onClick={() => onClose(false)}>{cancelText}</button>
        </div>
      </form>
    </dialog>
  )
}
