using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using DesktopFences.Services;

namespace DesktopFences;

public partial class App : Application
{
    Mutex? _mutex;
    bool _ownsMutex;
    FenceManager? _mgr;
    TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Elevated helper mode: hide/un-hide Public Desktop icons, then exit (no UI).
        if (e.Args.Length > 0 && e.Args[0] == "--apply-attrs")
        {
            Shutdown(DesktopHider.ApplyElevated(e.Args[1..]));
            return;
        }

        // --config <dir> lets you run a separate, sandboxed set of fences (handy for testing).
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopFences");
        for (int i = 0; i < e.Args.Length - 1; i++)
            if (e.Args[i].Equals("--config", StringComparison.OrdinalIgnoreCase))
                dir = Path.GetFullPath(e.Args[i + 1]);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dir.ToLowerInvariant())))[..16];
        _mutex = new Mutex(true, $"Local\\DesktopFences_{hash}", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("Desktop Fences is already running – look for its icon in the system tray.",
                "Desktop Fences", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Directory.CreateDirectory(dir);
        Log.Init(dir);
        Log.Info("Starting");
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log.Error("Unhandled exception", ex);
        };

        var store = new ConfigStore(dir);
        bool firstRun = !store.Exists;
        store.Load();

        _mgr = new FenceManager(store);
        _tray = new TrayIcon(_mgr);
        _mgr.Tray = _tray;
        _mgr.Start();
        if (firstRun) _mgr.FirstRun();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mgr?.Shutdown();
        _tray?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
