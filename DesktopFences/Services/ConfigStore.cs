using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using DesktopFences.Models;

namespace DesktopFences.Services;

/// <summary>Loads and (debounced) saves config.json, keeping a .bak of the previous good copy.</summary>
public sealed class ConfigStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    readonly DispatcherTimer _timer;

    public string Dir { get; }
    public string FilePath => Path.Combine(Dir, "config.json");
    public bool Exists => File.Exists(FilePath);
    public AppConfig Config { get; private set; } = new();

    public ConfigStore(string dir)
    {
        Dir = dir;
        Directory.CreateDirectory(dir);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _timer.Tick += (_, _) => SaveNow();
    }

    public void Load()
    {
        Config = TryRead(FilePath) ?? TryRead(FilePath + ".bak") ?? new AppConfig();
        Config.Settings ??= new AppSettings();
        Config.Fences ??= new();
        Config.HiddenByUs ??= new();
        Config.Layouts ??= new();
        Config.Rules ??= new();
        foreach (var f in Config.Fences)
        {
            f.Items ??= new();
            f.AutoCaptureExtensions ??= new();
        }
    }

    AppConfig? TryRead(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options);
        }
        catch (Exception ex)
        {
            Log.Error($"Reading {path}", ex);
            try { File.Copy(path, Path.Combine(Dir, $"config.corrupt-{DateTime.Now:yyyyMMddHHmmss}.json"), true); } catch { }
            return null;
        }
    }

    public void SaveSoon()
    {
        _timer.Stop();
        _timer.Start();
    }

    bool _idleSaveQueued;

    /// <summary>Saves as soon as the UI is idle (used for changes that must survive a crash).</summary>
    public void SaveAtIdle()
    {
        if (_idleSaveQueued) return;
        _idleSaveQueued = true;
        Dispatcher.CurrentDispatcher.InvokeAsync(() => { _idleSaveQueued = false; SaveNow(); }, DispatcherPriority.Background);
    }

    public void SaveNow()
    {
        _timer.Stop();
        try
        {
            var json = JsonSerializer.Serialize(Config, Options);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(FilePath))
                File.Replace(tmp, FilePath, FilePath + ".bak", true);
            else
                File.Move(tmp, FilePath);
        }
        catch (Exception ex)
        {
            Log.Error("Saving config", ex);
        }
    }
}
