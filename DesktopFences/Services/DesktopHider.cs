using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopFences.Interop;
using DesktopFences.Models;
using Microsoft.Win32;

namespace DesktopFences.Services;

/// <summary>
/// Hides fenced items from the real desktop without moving them.
/// Files/folders get Hidden+System ("protected"), which Explorer keeps hidden even when
/// "Show hidden items" is on. System icons (Recycle Bin, This PC…) use Explorer's
/// HideDesktopIcons setting. Items on the shared Public Desktop need admin rights, so they
/// are batched and applied by an elevated copy of this exe (one UAC prompt per batch).
///
/// Because the attributes travel with a file, anything that lets a fenced file leave the
/// desktop (drag out, cut/copy, delete) must call <see cref="ReleaseBits"/> first and
/// <see cref="Rehide"/> afterwards. Only items recorded in HiddenByUs are ever un-hidden.
/// </summary>
public sealed class DesktopHider
{
    const string HideIconsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
    const FileAttributes HideBits = FileAttributes.Hidden | FileAttributes.System;
    static readonly Regex GuidName = new(@"^::(\{[0-9A-Fa-f\-]{36}\})$");

    readonly AppConfig _cfg;
    readonly Dispatcher _ui;
    readonly Dictionary<string, bool> _elevatedQueue = new(StringComparer.OrdinalIgnoreCase); // path -> hide?
    bool _flushScheduled;
    bool _elevating;

    public static readonly string UserDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static readonly string PublicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    /// <summary>Should this item currently be hidden? (still fenced, hiding enabled…) Set by the manager.</summary>
    public Func<string, bool> StillWanted { get; set; } = _ => true;

    /// <summary>Set while "Put icons back" runs: nothing gets (re-)hidden.</summary>
    public bool Restoring { get; set; }

    bool Wanted(string p) => !Restoring && StillWanted(p);

    public bool HasPendingElevated => _elevatedQueue.Count > 0;

    /// <summary>Shell verbs that never copy, move or delete the file, so the bits can stay on.</summary>
    public static readonly HashSet<string> SafeVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "explore", "properties", "rename", "find", "preview", "enqueue", "mount", "install",
        "setdesktopwallpaper", "taskbarpin", "taskbarunpin", "compatibility", "windows.modernshare",
    };

    /// <summary>Verb prefixes that only open/show/launch the item (open, openas, edit, play, print, runas, pin…).</summary>
    static readonly string[] SafeVerbPrefixes = { "open", "edit", "play", "view", "print", "runas", "pin", "unpin" };

    public static bool IsSafeVerb(string? verb) =>
        verb != null && (SafeVerbs.Contains(verb) ||
                         SafeVerbPrefixes.Any(pre => verb.StartsWith(pre, StringComparison.OrdinalIgnoreCase)));

    /// <summary>A set of released files waiting to be re-hidden; follows renames.</summary>
    public sealed class ReleaseTicket
    {
        internal ReleaseTicket(List<string> paths) => Paths = paths;
        public List<string> Paths { get; }
        public bool IsEmpty => Paths.Count == 0;
    }

    readonly List<ReleaseTicket> _tickets = new();

    /// <summary>HiddenByUs changed – persist promptly so a crash can't orphan hidden files.</summary>
    public event Action? Changed;

    /// <summary>The user said no to the UAC prompt while hiding Public Desktop icons.</summary>
    public event Action? ElevationDeclined;

    /// <summary>Public Desktop items that could not be un-hidden (UAC declined or failed).</summary>
    public event Action<List<string>>? UnhideFailed;

    public static IEnumerable<string> DesktopFolders =>
        new[] { UserDesktop, PublicDesktop }.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d));

    public DesktopHider(AppConfig cfg, Dispatcher ui)
    {
        _cfg = cfg;
        _ui = ui;
    }

    public static bool IsOnDesktop(string p)
    {
        if (ShellInfo.IsShellGuid(p)) return GuidName.IsMatch(p);
        var dir = Path.GetDirectoryName(p);
        return dir != null && DesktopFolders.Any(d => SameDir(d, dir));
    }

    public static bool IsOnPublicDesktop(string p)
    {
        if (ShellInfo.IsShellGuid(p)) return false;
        var dir = Path.GetDirectoryName(p);
        return dir != null && !string.IsNullOrEmpty(PublicDesktop) && SameDir(PublicDesktop, dir);
    }

    static bool SameDir(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when Explorer shows protected (hidden+system) files, so fenced icons would still appear.</summary>
    public static bool ExplorerShowsProtectedFiles()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return k?.GetValue("ShowSuperHidden") is int v && v == 1;
        }
        catch { return false; }
    }

    public bool IsHiddenByUs(string p) => IndexOf(p) >= 0;

    int IndexOf(string p) => _cfg.HiddenByUs.FindIndex(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));

    static bool HasBits(string p)
    {
        try { return (File.GetAttributes(p) & HideBits) == HideBits; }
        catch { return false; }
    }

    static bool HasAnyBit(string p)
    {
        try { return (File.GetAttributes(p) & HideBits) != 0; }
        catch { return false; }
    }

    // ------------------------------------------------------------------ hide / unhide

    /// <summary>Returns false if the item can't be hidden.</summary>
    public bool Hide(string p)
    {
        if (Restoring) return false; // "Put icons back" is running: nothing new gets hidden
        try
        {
            var m = GuidName.Match(p);
            if (m.Success)
            {
                using var key = Registry.CurrentUser.CreateSubKey(HideIconsKey);
                var name = m.Groups[1].Value;
                if (key.GetValue(name) is int v && v == 1)
                    return true; // already hidden (by the user or by us earlier)
                Remember(p);
                key.SetValue(name, 1, RegistryValueKind.DWord);
                RefreshDesktop();
                return true;
            }

            if (!File.Exists(p) && !Directory.Exists(p)) return false;
            if (Path.GetFileName(p).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return false;

            // A pending elevated un-hide for this item is no longer wanted.
            if (_elevatedQueue.TryGetValue(p, out var queuedHide) && !queuedHide) _elevatedQueue.Remove(p);

            var attr = File.GetAttributes(p);
            if ((attr & HideBits) == HideBits)
            {
                // Already protected-hidden (by us, a previous run, or leftovers from another tool):
                // the user put it in a fence, so it's ours to manage from now on.
                Remember(p);
                return true;
            }

            if (IsOnPublicDesktop(p))
            {
                if (!_cfg.Settings.HidePublicDesktopIcons) return false;
                QueueElevated(p, hide: true);
                return true;
            }

            Remember(p); // record first: a crash between the two steps leaves a harmless record, not an orphan
            try { File.SetAttributes(p, attr | HideBits); }
            catch { Forget(p); throw; }
            Notify(p);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Hide {p}", ex);
            return false;
        }
    }

    public void Unhide(string p)
    {
        // Cancel a hide that is queued but not yet applied.
        if (_elevatedQueue.TryGetValue(p, out var queuedHide) && queuedHide) _elevatedQueue.Remove(p);
        int i = IndexOf(p);
        if (i < 0) return;
        try
        {
            var m = GuidName.Match(p);
            if (m.Success)
            {
                using var key = Registry.CurrentUser.CreateSubKey(HideIconsKey);
                key.SetValue(m.Groups[1].Value, 0, RegistryValueKind.DWord);
                RefreshDesktop();
            }
            else if (File.Exists(p) || Directory.Exists(p))
            {
                if (IsOnPublicDesktop(p) && HasAnyBit(p))
                {
                    QueueElevated(p, hide: false);
                    return; // forgotten once the elevated helper succeeds
                }
                var attr = File.GetAttributes(p);
                if ((attr & HideBits) != 0) File.SetAttributes(p, attr & ~HideBits);
                Notify(p);
            }
            Forget(p);
        }
        catch (Exception ex)
        {
            Log.Error($"Unhide {p}", ex);
        }
    }

    /// <summary>Drops queued (not yet applied) elevated hides for these paths.</summary>
    public void CancelQueuedHides(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (_elevatedQueue.TryGetValue(p, out var hide) && hide) _elevatedQueue.Remove(p);
    }

    public void UnhideAll()
    {
        // Drop hides that were queued but never applied, so they can't run after "put everything back".
        foreach (var k in _elevatedQueue.Where(e => e.Value).Select(e => e.Key).ToList()) _elevatedQueue.Remove(k);
        foreach (var p in _cfg.HiddenByUs.ToList()) Unhide(p);
    }

    public void Rename(string oldPath, string newPath)
    {
        foreach (var t in _tickets)
            for (int j = 0; j < t.Paths.Count; j++)
                if (string.Equals(t.Paths[j], oldPath, StringComparison.OrdinalIgnoreCase)) t.Paths[j] = newPath;
        int i = IndexOf(oldPath);
        if (i < 0) return;
        _cfg.HiddenByUs[i] = newPath;
        Changed?.Invoke();
    }

    public void Forget(string p)
    {
        int i = IndexOf(p);
        if (i < 0) return;
        _cfg.HiddenByUs.RemoveAt(i);
        Changed?.Invoke();
    }

    void Remember(string p)
    {
        if (IndexOf(p) >= 0) return;
        _cfg.HiddenByUs.Add(p);
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ files leaving the desktop

    /// <summary>
    /// Clears Hidden+System on our own user-desktop items so that a copy/move/recycle made from
    /// them doesn't carry the bits along. HiddenByUs is untouched; call <see cref="Rehide"/> after.
    /// Public Desktop items need elevation and are skipped. Returns the paths that were released.
    /// </summary>
    public ReleaseTicket ReleaseBits(IEnumerable<string> paths)
    {
        var released = new List<string>();
        var ticket = new ReleaseTicket(released);
        foreach (var p in paths)
        {
            if (!CanRelease(p)) continue;
            try
            {
                var attr = File.GetAttributes(p);
                if ((attr & HideBits) == 0)
                {
                    // Already released by an earlier operation that's still pending: this one shares it,
                    // so the bits only come back once *both* are done (see Rehide).
                    if (IsInLiveTicket(p)) released.Add(p);
                    continue;
                }
                File.SetAttributes(p, attr & ~HideBits);
                released.Add(p);
            }
            catch (Exception ex) { Log.Error($"Release {p}", ex); }
        }
        if (released.Count > 0) _tickets.Add(ticket);
        return ticket;
    }

    bool IsInLiveTicket(string p) =>
        _tickets.Any(t => t.Paths.Contains(p, StringComparer.OrdinalIgnoreCase));

    /// <summary>True for our own hidden files that can be released without admin rights.</summary>
    public bool CanRelease(string p) =>
        !ShellInfo.IsShellGuid(p) && IsHiddenByUs(p) && !IsOnPublicDesktop(p) && (File.Exists(p) || Directory.Exists(p));

    /// <summary>Re-hides released items that are still on the desktop and still fenced.</summary>
    public void Rehide(ReleaseTicket ticket)
    {
        _tickets.Remove(ticket);
        foreach (var p in ticket.Paths)
        {
            if (IsInLiveTicket(p)) continue; // another copy/cut is still using it
            if (!File.Exists(p) && !Directory.Exists(p)) continue; // moved/deleted: the watcher handles it
            if (IsHiddenByUs(p) && Wanted(p))
            {
                try
                {
                    var attr = File.GetAttributes(p);
                    File.SetAttributes(p, attr | HideBits);
                    Notify(p);
                }
                catch (Exception ex) { Log.Error($"Rehide {p}", ex); }
            }
            else
            {
                Forget(p); // no longer fenced: it stays visible
            }
        }
    }

    static void Notify(string p) =>
        Native.SHChangeNotifyPath(Native.SHCNE_UPDATEITEM, Native.SHCNF_PATHW | Native.SHCNF_FLUSHNOWAIT, p, IntPtr.Zero);

    static void RefreshDesktop() =>
        Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, Native.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

    // ------------------------------------------------------------------ Public Desktop (elevated)

    void QueueElevated(string p, bool hide)
    {
        _elevatedQueue[p] = hide;
        if (_flushScheduled) return;
        _flushScheduled = true;
        // Coalesce everything queued during this burst of work into a single UAC prompt.
        _ui.InvokeAsync(() => { _flushScheduled = false; _ = FlushElevatedAsync(); }, DispatcherPriority.ApplicationIdle);
    }

    async Task FlushElevatedAsync()
    {
        if (_elevating || _elevatedQueue.Count == 0) return;
        _elevating = true;
        var batch = _elevatedQueue.ToList();
        _elevatedQueue.Clear();
        try
        {
            // ShellExecute "runas" blocks until the UAC prompt is answered – keep the UI responsive.
            var outcome = await Task.Run(() => RunElevated(batch));
            Reconcile(batch, outcome);
        }
        finally
        {
            _elevating = false;
            if (_elevatedQueue.Count > 0) _ = FlushElevatedAsync();
        }
    }

    /// <summary>Applies queued Public Desktop changes right now, waiting for them (explicit user actions, quitting).</summary>
    public void FlushElevatedNow()
    {
        if (_elevatedQueue.Count == 0) return;
        var batch = _elevatedQueue.ToList();
        _elevatedQueue.Clear();
        Reconcile(batch, RunElevated(batch));
    }

    enum ElevationOutcome { Ran, Declined, Failed }

    static ElevationOutcome RunElevated(List<KeyValuePair<string, bool>> batch)
    {
        // Command lines are limited to 32K characters, so very large batches run in chunks.
        var chunks = new List<StringBuilder>();
        var sb = new StringBuilder("--apply-attrs");
        foreach (var (path, hide) in batch)
        {
            if (sb.Length + path.Length + 4 > 30_000)
            {
                chunks.Add(sb);
                sb = new StringBuilder("--apply-attrs");
            }
            sb.Append(" \"").Append(hide ? '+' : '-').Append(path.TrimEnd('\\')).Append('"');
        }
        chunks.Add(sb);

        foreach (var args in chunks)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args.ToString())
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                proc?.WaitForExit(120_000);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // user clicked "No" on UAC
            {
                return ElevationOutcome.Declined;
            }
            catch (Exception ex)
            {
                Log.Error("Elevated attribute helper", ex);
                return ElevationOutcome.Failed;
            }
        }
        return ElevationOutcome.Ran;
    }

    void Reconcile(List<KeyValuePair<string, bool>> batch, ElevationOutcome outcome)
    {
        var failedUnhides = new List<string>();
        var undoHides = new List<string>();
        var redoHides = new List<string>();
        foreach (var (path, hide) in batch)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) { Forget(path); continue; }
                bool hidden = HasBits(path);
                if (hide)
                {
                    if (!hidden) continue;
                    Remember(path);
                    // Taken out of its fence while the prompt was open? Then it must come back.
                    if (!Wanted(path)) undoHides.Add(path);
                }
                else if (!HasAnyBit(path))
                {
                    Forget(path);
                    // Put back into a fence while the prompt was open? Then hide it again.
                    if (Wanted(path)) redoHides.Add(path);
                }
                else
                {
                    failedUnhides.Add(path); // still in HiddenByUs, so "Put icons back" can retry it
                }
                Notify(path);
            }
            catch (Exception ex) { Log.Error($"Reconcile {path}", ex); }
        }

        if (outcome == ElevationOutcome.Declined && batch.Any(b => b.Value))
        {
            _cfg.Settings.HidePublicDesktopIcons = false;
            Changed?.Invoke();
            ElevationDeclined?.Invoke();
        }
        if (failedUnhides.Count > 0) UnhideFailed?.Invoke(failedUnhides);
        foreach (var p in undoHides) Unhide(p);
        foreach (var p in redoHides) Hide(p);
    }

    /// <summary>
    /// Entry point for the elevated helper: "+path" hides, "-path" un-hides. Only touches
    /// Hidden/System on items directly inside the Public Desktop folder.
    /// </summary>
    public static int ApplyElevated(IEnumerable<string> args)
    {
        int failures = 0;
        foreach (var arg in args)
        {
            if (arg.Length < 2 || (arg[0] != '+' && arg[0] != '-')) continue;
            try
            {
                var path = Path.GetFullPath(arg[1..]);
                if (!IsOnPublicDesktop(path) || (!File.Exists(path) && !Directory.Exists(path))) { failures++; continue; }
                var attr = File.GetAttributes(path);
                File.SetAttributes(path, arg[0] == '+' ? attr | HideBits : attr & ~HideBits);
            }
            catch { failures++; }
        }
        return failures == 0 ? 0 : 1;
    }
}
