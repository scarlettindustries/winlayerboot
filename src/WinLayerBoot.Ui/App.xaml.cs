using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace WinLayerBoot;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        // For exceptions in the UI, XAML only gives 0xc000027b with no cause: log them to a file.
        // The window does not exit with it: jobs run on background threads, and a UI error must not take down the base disk or child disk being built
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "wlb-gui-crash.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e.Message}\n{e.Exception}\n\n");
            }
            catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Only wlb gui gets here (see Program.Main, where the console from a double-click has already been released); other arguments are ignored
        _window = new MainWindow();
        _window.Activate();
    }
}
