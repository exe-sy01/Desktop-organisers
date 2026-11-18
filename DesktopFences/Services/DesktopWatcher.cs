using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Threading;

namespace DesktopFences.Services;

/// <summary>Watches the user's and the public desktop folders; events are raised on the UI thread.</summary>
public sealed class DesktopWatcher : IDisposable
{
    readonly List<FileSystemWatcher> _watchers = new();

    public event Action<string>? Created;
    public event Action<string>? Deleted;
    public event Action<string, string>? Renamed;

    public DesktopWatcher(Dispatcher ui)
    {
        foreach (var dir in DesktopHider.DesktopFolders)
        {
            try
            {
                var w = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                };
                w.Created += (_, e) => ui.InvokeAsync(() => Created?.Invoke(e.FullPath));
                w.Deleted += (_, e) => ui.InvokeAsync(() => Deleted?.Invoke(e.FullPath));
                w.Renamed += (_, e) => ui.InvokeAsync(() => Renamed?.Invoke(e.OldFullPath, e.FullPath));
                w.Error += (_, e) => Log.Error($"Watcher {dir}", e.GetException());
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex)
            {
                Log.Error($"Watching {dir}", ex);
            }
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }
}
