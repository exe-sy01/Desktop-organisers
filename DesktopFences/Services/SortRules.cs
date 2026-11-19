using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DesktopFences.Models;

namespace DesktopFences.Services;

/// <summary>Decides whether a new desktop item matches a <see cref="SortRule"/>.</summary>
public static class SortRules
{
    public static readonly Dictionary<FileCategory, string[]> Extensions = new()
    {
        [FileCategory.Documents] = new[] { ".pdf", ".doc", ".docx", ".txt", ".rtf", ".odt", ".xls", ".xlsx", ".csv", ".ppt", ".pptx", ".md", ".json", ".xml", ".log", ".ods", ".odp" },
        [FileCategory.Images] = new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".heic", ".tif", ".tiff", ".psd", ".avif" },
        [FileCategory.MusicAndVideo] = new[] { ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma" },
        [FileCategory.WebLinks] = new[] { ".url", ".website" },
        [FileCategory.ProgramShortcuts] = new[] { ".lnk", ".appref-ms", ".exe" },
    };

    public static string CategoryName(FileCategory c) => c switch
    {
        FileCategory.Folders => "Folders",
        FileCategory.Documents => "Documents",
        FileCategory.Images => "Images",
        FileCategory.MusicAndVideo => "Music & video",
        FileCategory.Downloads => "Downloaded files",
        FileCategory.WebLinks => "Web links",
        FileCategory.ProgramShortcuts => "Program shortcuts",
        _ => c.ToString(),
    };

    public static bool Matches(SortRule rule, string path)
    {
        if (!rule.Enabled || ShellInfo.IsShellGuid(path)) return false;
        try
        {
            var name = Path.GetFileName(path);
            var shown = Path.GetExtension(path) is ".lnk" or ".url" ? Path.GetFileNameWithoutExtension(path) : name;
            var value = rule.Value.Trim();
            bool isDir = Directory.Exists(path);
            switch (rule.Kind)
            {
                case RuleKind.FileType:
                    if (!Enum.TryParse<FileCategory>(value, out var cat)) return false;
                    if (cat == FileCategory.Folders) return isDir;
                    if (isDir) return false;
                    if (cat == FileCategory.Downloads) return IsDownloaded(path);
                    return Extensions.TryGetValue(cat, out var exts) &&
                           exts.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

                case RuleKind.NameContains:
                    return value.Length > 0 && shown.Contains(value, StringComparison.OrdinalIgnoreCase);
                case RuleKind.NameStartsWith:
                    return value.Length > 0 && shown.StartsWith(value, StringComparison.OrdinalIgnoreCase);
                case RuleKind.NameEndsWith:
                    return value.Length > 0 && shown.EndsWith(value, StringComparison.OrdinalIgnoreCase);

                case RuleKind.ShortcutTarget:
                    return value.Length > 0 && ShortcutTarget(path) is { } target &&
                           target.Contains(value, StringComparison.OrdinalIgnoreCase);

                case RuleKind.CreatedBetweenHours:
                    if (!TryParseHours(value, out int from, out int to)) return false;
                    int hour = Created(path).Hour;
                    return from <= to ? hour >= from && hour < to : hour >= from || hour < to; // "22-6" wraps midnight

                case RuleKind.CreatedOnDays:
                    var day = Created(path).DayOfWeek.ToString()[..3];
                    return value.Split(',', ';', ' ').Any(d => d.Trim().Length >= 3 &&
                        d.Trim()[..3].Equals(day, StringComparison.OrdinalIgnoreCase));

                case RuleKind.SizeLargerThan:
                    return !isDir && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double mb) &&
                           new FileInfo(path).Length > mb * 1024 * 1024;
            }
        }
        catch (Exception ex) { Log.Error($"Rule check for {path}", ex); }
        return false;
    }

    public static bool TryParseHours(string s, out int from, out int to)
    {
        from = to = 0;
        var parts = s.Split('-', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out from) && int.TryParse(parts[1], out to) &&
               from is >= 0 and <= 24 && to is >= 0 and <= 24 && from != to;
    }

    static DateTime Created(string path) =>
        Directory.Exists(path) ? Directory.GetCreationTime(path) : File.GetCreationTime(path);

    /// <summary>Files downloaded from the internet carry a "Zone.Identifier" stream (Mark of the Web).</summary>
    public static bool IsDownloaded(string path)
    {
        try
        {
            using var s = new FileStream(path + ":Zone.Identifier", FileMode.Open, FileAccess.Read);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Where a .lnk shortcut or .url link points.</summary>
    public static string? ShortcutTarget(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".url" or ".website")
                return File.ReadLines(path).FirstOrDefault(l => l.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?[4..];
            if (ext != ".lnk") return null;
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic link = shell.CreateShortcut(path);
                string target = link.TargetPath;
                string args = link.Arguments;
                return string.IsNullOrEmpty(args) ? target : target + " " + args;
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        catch { return null; }
    }

    public static string Describe(SortRule r, Func<string, string> fenceName)
    {
        string what = r.Kind switch
        {
            RuleKind.FileType => Enum.TryParse<FileCategory>(r.Value, out var c) ? CategoryName(c) : "Files of an unknown type",
            RuleKind.NameContains => $"Names containing \"{r.Value}\"",
            RuleKind.NameStartsWith => $"Names starting with \"{r.Value}\"",
            RuleKind.NameEndsWith => $"Names ending with \"{r.Value}\"",
            RuleKind.ShortcutTarget => $"Shortcuts pointing to \"{r.Value}\"",
            RuleKind.CreatedBetweenHours => $"Created between {r.Value.Replace("-", ":00 and ")}:00",
            RuleKind.CreatedOnDays => $"Created on {r.Value}",
            RuleKind.SizeLargerThan => $"Files larger than {r.Value} MB",
            _ => r.Kind.ToString(),
        };
        return $"{what}  →  {fenceName(r.TargetFenceId)}";
    }
}
