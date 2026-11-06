# Third-party notices

The WinLayerBoot source code is under the MIT license in [LICENSE](LICENSE). The released `wlb.exe` and `wlb-ui.exe` also contain the third-party components below, which stay under their own licenses. The license texts are in [licenses/](licenses/). Keep this file, `LICENSE` and `licenses/` together with the exes when redistributing them.

This list is conservative: `wlb-ui.exe` is trimmed at publish time, so some components may be partly or fully removed from it.

## Microsoft Windows App SDK (not open source)

`wlb-ui.exe` bundles the Microsoft Windows App SDK, including WinUI 3 and related components. These are not covered by the MIT license. They are licensed under the Microsoft Software License Terms in `licenses/WindowsAppSDK-license.txt`, with the Microsoft WebView2 and Windows AI Machine Learning terms alongside it. **By using `wlb-ui.exe` you agree to those terms.** This software comes from an independent project and is not made or endorsed by Microsoft.

`wlb.exe` doesn't contain these components.

| Component | Terms | Notices |
|---|---|---|
| Windows App SDK, WinUI 3 | `WindowsAppSDK-license.txt` | `WindowsAppSDK-NOTICE.txt`, `WindowsAppSDK.WinUI-NOTICE.txt` |
| Windows App SDK ML | `WindowsAppSDK.ML-license.txt` | |
| WebView2 | `WebView2-LICENSE.txt` | `WebView2-NOTICE.txt` |
| Windows AI Machine Learning | `Windows.AI.MachineLearning-license.txt` | `Windows.AI.MachineLearning-ThirdPartyNotices.txt` |

## Open source

| Component | License | In | License text |
|---|---|---|---|
| .NET runtime and libraries | MIT | both exes | `dotnet-LICENSE.txt`, `dotnet-ThirdPartyNotices.txt` |
| Costura | MIT | `wlb.exe` | `Costura-LICENSE.txt` |
| PolySharp (generated code) | MIT | both exes | `PolySharp-LICENSE.txt` |
| React, React DOM, scheduler | MIT | `wlb.exe` (web page) | `React-LICENSE.txt` |
| Terminal.Gui | MIT | `wlb-ui.exe` | `Terminal.Gui-LICENSE.txt` |
| ColorHelper | MIT | `wlb-ui.exe` | `ColorHelper-LICENSE.txt` |
| JetBrains.Annotations | MIT | `wlb-ui.exe` | `JetBrains.Annotations-LICENSE.txt` |
| Markdig | BSD-2-Clause | `wlb-ui.exe` | `Markdig-LICENSE.txt` |
| Onigwrap, Oniguruma | MIT, BSD-2-Clause | `wlb-ui.exe` | `Onigwrap-LICENSE.txt`, `Onigwrap-THIRD-PARTY-NOTICES.txt` |
| System.IO.Abstractions | MIT | `wlb-ui.exe` | `System.IO.Abstractions-LICENSE.txt` |
| Testably.Abstractions | MIT | `wlb-ui.exe` | `Testably.Abstractions-LICENSE.txt` |
| TextMateSharp, TextMateSharp.Grammars | MIT | `wlb-ui.exe` | `TextMateSharp-LICENSE.txt` |
| Wcwidth | MIT | `wlb-ui.exe` | `Wcwidth-LICENSE.txt` |
