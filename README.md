# Desktop Fences

A small, free, open-source desktop organiser for Windows 10/11. It groups your desktop icons into translucent, resizable panels ("fences") that sit on the wallpaper behind your windows. It's an alternative to Stardock Fences, built with C# and WPF on .NET 8.

## Download

Get `DesktopFences.exe` from the [Releases](../../releases) page and run it. There's no installer.

It needs the **[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)** (x64). If the runtime is missing, Windows will offer to download it.

On the first run, it offers to sort your desktop into fences automatically.

> **Coming from Stardock Fences?** Remove your fences in Fences (or uninstall it) so your icons are back on the plain desktop, and quit it before starting Desktop Fences.

## Features

| | |
|---|---|
| **Fences** | Drag icons from the desktop into a fence. Drag them out onto the desktop to take them out of the fence. Drag between fences, or within a fence to rearrange. |
| **Real icons, real menus** | Double-click opens. Right-click shows the normal Explorer menu (Open with, Send to, Properties…) plus "Remove from fence". Delete = Recycle Bin, F2 = rename, Enter = open. |
| **Folder portals** | A fence that shows the live contents of any folder. Dropping files on it moves them into that folder (hold Ctrl to copy). |
| **Roll up** | Double-click a fence's title to roll it up to just the title bar. It unrolls while your mouse is over it. |
| **Quick-hide** | Double-click empty desktop space, or left-click the tray icon, to hide or show all fences. |
| **Auto-capture rules** | Right-click a fence and choose **Customize…**, then list extensions (e.g. `.lnk, .url`). New desktop files with those extensions go straight into that fence. `*` catches anything no other fence claims. |
| **Auto-organise** | From the tray menu. Creates Shortcuts / Folders / Documents / Images & Media / Installers & Archives / Other fences. |
| **Look** | Per-fence colour and opacity, rename, sort (manual, name, type, date), lock position. You can choose the icon size in Settings. |
| **Snapping** | Fences snap to screen edges and to each other while you move or resize them. Hold Alt to turn snapping off. |
| **Show desktop** | Fences stay visible after Win+D. |
| **Start with Windows** | Turn it on in the tray menu under Settings. |


## Preview of settings

<img width="1147" height="712" alt="Screenshot_31" src="https://github.com/user-attachments/assets/00a14b5a-096c-4e39-b9d2-e72e51cc6fa7" />


## How hiding works

Nothing is moved or deleted. A file you put in a fence gets the *Hidden + System* attributes. Explorer then keeps it off the bare desktop, even when "Show hidden items" is on. System icons like the Recycle Bin use Explorer's own "hide desktop icon" setting instead.

The app keeps a list of what it hid and only ever un-hides those items. Before a fenced file is copied, moved or deleted, it takes the attributes off, so the result isn't invisible. It puts them back once the operation is done.

- **Put icons back on desktop & quit** (tray menu) restores everything. Use it before uninstalling.
- Shortcuts on the shared *Public* desktop (`C:\Users\Public\Desktop`) need admin rights to change. These changes are batched into a single UAC prompt. If you decline, those shortcuts stay visible on the desktop and still work in fences.
- If Explorer is set to show *protected operating system files*, fenced icons will still appear, faded.

## Data

| What | Where |
|---|---|
| Settings and layout | `%APPDATA%\DesktopFences\config.json` (a `.bak` copy of the previous version is kept next to it) |
| Log | `%APPDATA%\DesktopFences\log.txt` |

## Building

Requires the .NET 8 SDK or newer.

```bash
dotnet publish DesktopFences -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Run the checks for the desktop-hiding logic (they only touch files in a temp folder):

```bash
dotnet run --project tests/DesktopFences.Tests
```

To try changes without touching your real fences, run the app with its own config folder:

```bash
DesktopFences.exe --config C:\temp\fences-test
```

Releases are built by GitHub Actions ([.github/workflows/build.yml](.github/workflows/build.yml)). Pushing a tag such as `v1.0.0` builds the exe and attaches it to a GitHub Release.

### Project layout

```
DesktopFences/
  App.xaml(.cs)          startup, single instance, elevated helper mode (--apply-attrs)
  FenceManager.cs        owns all fences: hiding, desktop watcher, z-order, snapping, auto-organise
  FenceWindow.xaml(.cs)  one fence: items, drag & drop, roll-up, menus
  TrayIcon.cs            notification-area icon and menu
  Dialogs/               fence customisation, settings, rename
  Services/              desktop hiding, shell icons/menus, file watching, config, Win+D handling
  Interop/               Win32 / shell COM declarations
tests/DesktopFences.Tests/  checks for the hide / release / re-hide logic
```

## Disclaimer

This is an independent project, not affiliated with or endorsed by Stardock. "Fences" is a trademark of Stardock Corporation.

## License

[MIT](LICENSE)
