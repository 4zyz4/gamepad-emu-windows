using System.Windows;
using GamepadEmuHost.Core;
using GamepadEmuHost.Ui;

namespace GamepadEmuHost;

public partial class App : Application
{
    public NetworkService? Service { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            Service = new NetworkService();
        }
        catch
        {
            // ViGEm driver not installed — service unavailable
        }

        var vm = new MainViewModel(Service);
        var window = new MainWindow(vm);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Service?.Dispose(); }
        catch { }
        base.OnExit(e);
    }
}
