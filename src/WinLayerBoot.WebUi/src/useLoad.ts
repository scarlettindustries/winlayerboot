import { useCallback, useRef, useState } from 'react'

/** List pages read data: with a loading flag; if refresh is clicked several times in a row, only the last counts. fetch must be a stable function (such as api.overview). */
export function useLoad<T>(fetch: () => Promise<T>) {
  const [data, setData] = useState<T | null>(null)
  const [err, setErr] = useState('')
  const [busy, setBusy] = useState(false)
  const seq = useRef(0)
  const load = useCallback(() => {
    const s = ++seq.current
    setBusy(true)
    setErr('')
    fetch().then(d => { if (s === seq.current) setData(d) })
      .catch(e => { if (s === seq.current) setErr((e as Error).message) })
      .finally(() => { if (s === seq.current) setBusy(false) })
  }, [fetch])
  return { data, err, busy, load }
}
