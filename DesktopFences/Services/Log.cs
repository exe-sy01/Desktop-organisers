using System;
using System.IO;

namespace DesktopFences.Services;

public static class Log
{
    static string? _path;
    static readonly object Gate = new();

    public static void Init(string dir)
    {
        _path = Path.Combine(dir, "log.txt");
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length > 512 * 1024)
                File.Delete(_path);
        }
        catch { /* ignore */ }
    }

    public static void Info(string msg) => Write("INFO", msg);

    public static void Error(string context, Exception ex) => Write("ERROR", $"{context}: {ex}");

    static void Write(string level, string msg)
    {
        if (_path == null) return;
        try
        {
            lock (Gate)
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }
}
