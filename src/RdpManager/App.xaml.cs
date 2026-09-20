using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using RdpManager.Services;
using RdpManager.Views;

namespace RdpManager;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Each instance saves its own full list over the same file, so a second one would quietly
        // drop whatever the first had added. The name is tied to the store's location, so separate
        // copies of the app in different folders can still run side by side. The hash has to be a
        // stable one: string.GetHashCode is randomised per process, so every instance would get a
        // different name and none of them would ever see each other.
        var pathBytes = Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToLowerInvariant());
        var storeKey = Convert.ToHexString(SHA256.HashData(pathBytes))[..16];
        _singleInstance = new Mutex(true, $@"Local\RdpManager.Store.{storeKey}", out var isOnlyInstance);

        if (!isOnlyInstance)
        {
            MessageBox.Show(
                "A RDP Manager már fut ebből a mappából. Egyszerre csak egy példány használhatja a tárolót, "
                + "mert különben felülírnák egymás módosításait.",
                "RDP Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

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
