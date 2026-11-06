using System;
using System.Linq;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinLayerBoot.Core;

namespace WinLayerBoot;

/// <summary>
/// Entry point of wlb-ui.exe (the project defines DISABLE_XAML_GENERATED_MAIN):
///   wlb-ui tui         start the TUI
///   wlb-ui, wlb-ui gui start the WinUI window
/// The command line and web interface are in wlb.exe.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var tui = args.Length > 0 && args[0].TrimStart('-', '/').Equals("tui", StringComparison.OrdinalIgnoreCase);
        // The console from a double-click must be released before setting the code page: later child processes rely on that to not open windows (see Proc.InitCodePage)
        if (!tui) ConsoleWrite.DetachIfOwnConsole();
        Proc.InitCodePage();
        try
        {
            return tui ? Tui.TuiApp.Run(args.Skip(1).ToArray()) : StartGui();
        }
        finally { Proc.RestoreCodePage(); }
    }

    private static int StartGui()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
