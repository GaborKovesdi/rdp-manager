using System.Windows;
using RdpManager.Services;
using RdpManager.Views;

namespace RdpManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Without this the app would exit when the master password dialog closes, before the
        // main window has ever been shown.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var store = new ConnectionStore();
        var prompt = new MasterPasswordWindow(store);

        if (prompt.ShowDialog() != true)
        {
            store.Dispose();
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnLastWindowClose;
        MainWindow = new MainWindow(store, prompt.Connections);
        MainWindow.Show();
    }
}
