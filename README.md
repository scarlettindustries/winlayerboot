# WinLayerBoot

A tool for building layered VHDX disks that boot Windows natively. One repository, four projects:

| Project | Target | Contents | Output |
|---|---|---|---|
| `src/WinLayerBoot.Core` | `net48` + `net10.0-windows` | Operations, disks, BCD, customization (one source tree, compiled twice) | Library |
| `src/WinLayerBoot.Cli` | `net48` | Command line, web server, embedded page | `wlb.exe`, no runtime to install |
| `src/WinLayerBoot.Ui` | `net10.0-windows` | TUI, WinUI 3 window | `wlb-ui.exe`, bundles its runtime |
| `src/WinLayerBoot.WebUi` | bun + React | Web frontend, built into `src/WinLayerBoot.Cli/wwwroot` | Static files |

`wlb gui` and `wlb tui` start the `wlb-ui.exe` next to `wlb.exe`. Both exes must come from the same commit; if they don't, the UI won't start.

## Account defaults

The default username, password and SSH public key are empty in the source. Set them in Settings (or with `wlb config User=...`). They are saved on the local machine in `%ProgramData%\WinLayerBoot\config.json`, not in the repository, and fill in the form when you create a child disk. Exported preset files don't contain the password; on import it comes from the defaults in Settings.

## Build

```
dotnet publish src\WinLayerBoot.Cli\WinLayerBoot.Cli.csproj -c Release -p:Platform=x64
dotnet publish src\WinLayerBoot.Ui\WinLayerBoot.Ui.csproj -p:PublishProfile=win-x64
```

For the frontend, see [src/WinLayerBoot.WebUi/README.md](src/WinLayerBoot.WebUi/README.md).

## License

The source code is under the [MIT license](LICENSE). The released exes also contain third-party components under their own licenses, including Microsoft's closed-source Windows App SDK in `wlb-ui.exe`; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Keep `LICENSE`, that file and `licenses/` with the exes when redistributing them.
