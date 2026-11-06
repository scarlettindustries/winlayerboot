// Line icons for the navigation bar (24×24, stroked with the current text color)
const PATHS: Record<string, string> = {
  overview: 'M3 11l9-8 9 8M5 10v10h5v-6h4v6h5V10',
  base: 'M12 3l9 5-9 5-9-5 9-5zM3 13l9 5 9-5M3 17l9 5 9-5',
  layer: 'M8 3h11a2 2 0 012 2v11M5 7h10a2 2 0 012 2v10a2 2 0 01-2 2H5a2 2 0 01-2-2V9a2 2 0 012-2z',
  snapshot: 'M3 8a2 2 0 012-2h3l2-3h4l2 3h3a2 2 0 012 2v10a2 2 0 01-2 2H5a2 2 0 01-2-2V8zM12 17a4 4 0 100-8 4 4 0 000 8z',
  export: 'M12 16V4M7 9l5-5 5 5M4 15v4a2 2 0 002 2h12a2 2 0 002-2v-4',
  display: 'M3 5h18v12H3zM8 21h8M12 17v4',
  entries: 'M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01',
  settings: 'M12 15a3 3 0 100-6 3 3 0 000 6zM19 12l2-1-2-4-2 1-2-1V5h-6v2L7 8 5 7l-2 4 2 1v2l-2 1 2 4 2-1 2 1v2h6v-2l2-1 2 1 2-4-2-1v-2z',
}

export function Icon({ name }: { name: string }) {
  return <svg className="nav-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={PATHS[name]} /></svg>
}
