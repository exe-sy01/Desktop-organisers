// Checks for DesktopHider: hiding, releasing for copies/moves, re-hiding, and restore mode.
// Works only on files in a temp folder – never on the real desktop.
using System.IO;
using System.Windows.Threading;
using DesktopFences.Models;
using DesktopFences.Services;

int fails = 0;
void Check(bool ok, string name)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (!ok) fails++;
}

const FileAttributes HS = FileAttributes.Hidden | FileAttributes.System;
bool Bits(string p) => (File.GetAttributes(p) & HS) == HS;
bool NoBits(string p) => (File.GetAttributes(p) & HS) == 0;

var dir = Path.Combine(Path.GetTempPath(), "dftest_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
string NewFile(string name)
{
    var p = Path.Combine(dir, name);
    File.WriteAllText(p, "x");
    return p;
}

try
{
    var h = new DesktopHider(new AppConfig(), Dispatcher.CurrentDispatcher);
    int changed = 0;
    h.Changed += () => changed++;
    bool wanted = true;
    h.StillWanted = _ => wanted;

    var a = NewFile("a.lnk");
    Check(h.Hide(a) && Bits(a) && h.IsHiddenByUs(a), "Hide sets Hidden+System and records it");
    Check(changed > 0, "Changed fires when the record changes");

    var pre = NewFile("pre.url");
    File.SetAttributes(pre, FileAttributes.Hidden);
    Check(h.Hide(pre) && Bits(pre) && h.IsHiddenByUs(pre), "An already-hidden file (e.g. left by another tool) is taken over");

    var t = h.ReleaseBits(new[] { a });
    Check(t.Paths.Count == 1 && NoBits(a) && h.IsHiddenByUs(a), "ReleaseBits clears the bits but keeps tracking");
    var copy = Path.Combine(dir, "copy.lnk");
    File.Copy(a, copy);
    Check(NoBits(copy), "A copy made while released is visible");
    h.Rehide(t);
    Check(Bits(a) && h.IsHiddenByUs(a), "Rehide restores the bits while still fenced");

    t = h.ReleaseBits(new[] { a });
    wanted = false;
    h.Rehide(t);
    wanted = true;
    Check(NoBits(a) && !h.IsHiddenByUs(a), "Rehide leaves it visible and forgets it once no longer fenced");

    var moved = NewFile("gone.lnk");
    h.Hide(moved);
    t = h.ReleaseBits(new[] { moved });
    var dest = Path.Combine(dir, "elsewhere.lnk");
    File.Move(moved, dest);
    h.Rehide(t);
    Check(NoBits(dest), "A file moved away while released stays visible at its destination");

    Check(h.ReleaseBits(new[] { copy }).IsEmpty, "ReleaseBits ignores files we didn't hide");

    // Two overlapping operations (e.g. a drag-out, then Copy) share the release.
    var o = NewFile("overlap.lnk");
    h.Hide(o);
    var t1 = h.ReleaseBits(new[] { o });
    var t2 = h.ReleaseBits(new[] { o });
    Check(t2.Paths.Count == 1, "A second release of an already-released file joins it");
    h.Rehide(t1);
    Check(NoBits(o), "The first operation finishing doesn't re-hide while the second still needs it");
    h.Rehide(t2);
    Check(Bits(o), "Re-hidden once both operations are done");

    // Rename while released: the pending re-hide follows the new name.
    var r1 = NewFile("ren.lnk");
    h.Hide(r1);
    var tr = h.ReleaseBits(new[] { r1 });
    var r2 = Path.Combine(dir, "ren2.lnk");
    File.Move(r1, r2);
    h.Rename(r1, r2);
    h.Rehide(tr);
    Check(Bits(r2) && h.IsHiddenByUs(r2), "Rename while released: re-hide follows the new name");

    // Restore mode ("Put icons back & quit"): nothing gets hidden or re-hidden.
    var rs = NewFile("restore.lnk");
    h.Hide(rs);
    var trs = h.ReleaseBits(new[] { rs });
    h.Restoring = true;
    h.Rehide(trs);
    var fresh = NewFile("fresh.lnk");
    bool hidFresh = h.Hide(fresh);
    h.Restoring = false;
    Check(NoBits(rs) && !h.IsHiddenByUs(rs), "Restoring blocks re-hide");
    Check(!hidFresh && NoBits(fresh), "Restoring blocks new hides");

    h.Unhide(pre);
    Check(NoBits(pre) && !h.IsHiddenByUs(pre), "Unhide clears the bits and forgets the item");

    var ini = NewFile("desktop.ini");
    Check(!h.Hide(ini) && NoBits(ini), "desktop.ini is never touched");

    var outside = NewFile("outside.lnk");
    Check(DesktopHider.ApplyElevated(new[] { "+" + outside }) != 0 && NoBits(outside),
        "The elevated helper refuses paths outside the Public Desktop");

    var folder = Path.Combine(dir, "Folder");
    Directory.CreateDirectory(folder);
    Check(h.Hide(folder) && Bits(folder), "Folders can be hidden");
    h.Unhide(folder);
    Check(NoBits(folder), "Folders un-hide cleanly");

    Check(DesktopHider.IsSafeVerb("open") && DesktopHider.IsSafeVerb("openas") && DesktopHider.IsSafeVerb("play") &&
          DesktopHider.IsSafeVerb("properties") && DesktopHider.IsSafeVerb("pintostartscreen"),
        "Open / Open with / Play / Properties / Pin are safe verbs");
    Check(!DesktopHider.IsSafeVerb("copy") && !DesktopHider.IsSafeVerb("cut") && !DesktopHider.IsSafeVerb("delete") &&
          !DesktopHider.IsSafeVerb(null),
        "Copy / Cut / Delete / unknown commands are not safe");
    Check(!h.HasPendingElevated, "Nothing needs admin rights for files outside the Public Desktop");
}
finally
{
    foreach (var f in Directory.GetFileSystemEntries(dir)) File.SetAttributes(f, FileAttributes.Normal);
    Directory.Delete(dir, true);
}

Console.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
return fails == 0 ? 0 : 1;
