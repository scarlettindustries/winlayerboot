# WinLayerBoot.WebUi: the WinLayerBoot web interface

The frontend of `wlb webui`, built with Bun, Vite, React and TypeScript. It is fully static: the build output goes to `../WinLayerBoot.Cli/wwwroot` and is embedded in `wlb.exe` at compile time. At run time, the HttpListener inside `wlb.exe` serves it, with the API under `/api/`.

## Development

Edit the frontend on a machine with bun. The backend compiles on a Windows build machine, which doesn't have bun.

```sh
bun install
bun dev                     # http://<host>:5173/, proxies the API to WLB_API
```

Before `bun dev`, copy `.env.example` to `.env.local` and fill it in. That file stays out of git.

## Embedding in wlb.exe

```sh
bun run build               # builds into ../WinLayerBoot.Cli/wwwroot; doesn't commit
```

Commit the changes to `src/WinLayerBoot.WebUi` and `src/WinLayerBoot.Cli/wwwroot`, pull them on the build machine, and run `dotnet publish src\WinLayerBoot.Cli\WinLayerBoot.Cli.csproj -c Release -p:Platform=x64`. Publishing also updates `V:\wlb.exe`.

## Pages

The pages match the WinUI window (`wlb gui`) and the TUI (`wlb tui`): overview (inheritance tree), base disk, child disk, snapshot, export, display, boot entries and settings. Each operation page draws its form from the field list and check results returned by `/api/ops/<name>/check`, and re-checks as you edit. "Export preset" produces the same JSON as the CLI's `--save` and the other interfaces' exports, and all four can import it.
