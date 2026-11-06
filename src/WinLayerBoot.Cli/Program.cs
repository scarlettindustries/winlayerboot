using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using WinLayerBoot.Core;

namespace WinLayerBoot;

/// <summary>
/// Entry point of wlb.exe:
///   wlb webui          start the web interface
///   wlb gui, wlb tui   start the adjacent wlb-ui.exe (the WinUI window and TUI run on .NET 10, in a separate exe)
///   wlb                no arguments: opens the window when started by double-click, prints help when started from a terminal
///   anything else      CLI
/// </summary>
public static class Program
{
    private static int Main(string[] args)
    {
        var cmd = args.Length > 0 ? args[0].TrimStart('-', '/').ToLowerInvariant() : "";
        // With no arguments, look at the console: a console from double-click is newly created and has only this process
        var doubleClicked = args.Length == 0 && !Console.IsOutputRedirected && ConsoleWrite.OwnsConsole();
        if (cmd is "gui" or "tui" || doubleClicked) return StartUi(cmd == "tui" ? "tui" : "gui");
        Proc.InitCodePage();
        try
        {
            return cmd switch
            {
                "webui" => Web.WebServer.Run(args.Skip(1).ToArray()),
                _ => Cli.CliApp.Run(args),
            };
        }
        finally { Proc.RestoreCodePage(); }
    }

    /// <summary>The two exes share the lock and job files, so they must come from the same commit: refuse to start if they differ.</summary>
    private static int StartUi(string mode)
    {
        var self = Process.GetCurrentProcess().MainModule!.FileName!;
        var ui = Path.Combine(Path.GetDirectoryName(self)!, "wlb-ui.exe");
        if (!File.Exists(ui)) { ConsoleWrite.Write($"Error: cannot find {ui}. The window and TUI are in wlb-ui.exe; the command line and webui do not need it\n"); return 1; }
        var mine = FileVersionInfo.GetVersionInfo(self).ProductVersion;
        var theirs = FileVersionInfo.GetVersionInfo(ui).ProductVersion;
        if (mine != theirs) { ConsoleWrite.Write($"Error: {ui} and wlb.exe are not from the same build ({theirs} / {mine}); update them together\n"); return 1; }
        using var p = Process.Start(new ProcessStartInfo(ui, mode) { UseShellExecute = false })!;
        if (mode == "gui") return 0;
        p.WaitForExit();
        return p.ExitCode;
    }
}
