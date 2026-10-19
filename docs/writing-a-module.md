# Writing a module

From nothing to a tile on the Tools page. The YouTube downloader in `src/modules/youtube` is a
complete example of a single-part tool, Storage in `src/modules/storage` of a tool made of
sections; this page says what is required and why.

**Tool or feature?** A module is a **tool**: something opened when needed, with a view. Anything
that improves Windows without a window (a shortcut, a watcher, a notice) is a **feature** and
lives in the app itself, `src/MultiTool.Host/Features`, not in a module. A feature may load a tool
quietly when it has real work, the way Win+Shift+S loads Capture.

## 1. Folder and project file

The **folder name is the module id**. Settings, status file, data folder and command line all
address the module by it, and the catalog rejects a module whose folder name differs from
its id.

```
src/modules/my-module/
    MultiTool.Modules.MyModule.csproj
    module.json
    MyModule.cs
    MyModuleView.axaml
    MyModuleView.axaml.cs
```

The project file is nearly empty. Everything module-specific lives in
`src/modules/Directory.Build.props` and applies automatically:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>MultiTool.Modules.MyModule</RootNamespace>
    <AssemblyName>MultiTool.Modules.MyModule</AssemblyName>
  </PropertyGroup>
</Project>
```

**The assembly name must start with `MultiTool.Modules.` and be unique.** Avalonia resolves
`avares://` by the *simple* assembly name and takes the first match in the process; two
modules called `Tools` would wire up each other's resources.

Your own NuGet packages work as usual; they land in the module folder and are only loaded by
this module:

```xml
<ItemGroup>
  <PackageReference Include="NAudio" />
</ItemGroup>
```

The version goes into `src/Directory.Packages.props`. If a package needs a framework the host
doesn't reference (WinForms, WPF), **the host** must reference it: a module's
`.runtimeconfig.json` is ignored when loading through a load context.

Finally add the project to `MultiTool.slnx`.

## 2. The manifest

`module.json` is the only file read at startup. Everything in it can be evaluated without
module code; that is its whole purpose.

```json
{
  "id": "my-module",
  "name": "My module",
  "summary": "One sentence. Shown below the name on the tile.",
  "category": "Tools",
  "order": 65,

  "icon": "M12 2a5 5 0 0 0-5 5v3H6a2 2 0 0 0-2 2v8a2 2 …",

  "assembly": "MultiTool.Modules.MyModule.dll",
  "entryType": "MultiTool.Modules.MyModule.MyModule",

  "keywords": ["search term", "another one"],

  "elevation": "never",
  "keepsRunning": false,

  "status": { "kind": "none" }
}
```

**`order`** is relevance across all tools since 0.13.0: the Tools page and the dashboard sort by
it, pinned tools first. Capture is 10, Apps 20, Storage 30; a niche tool goes near the end.

**The icon** is the `d` attribute of an SVG path in a 24×24 box. No image file, no resource
reference: loading it from the assembly would load every module to draw the start page.

**`elevation`**: `never` · `onDemand` (works normally, single actions request rights) ·
`required` (opening it restarts the app elevated with the module open).

**`keepsRunning`**: whether the module may keep working while its view is closed. A running
download yes, a password generator no. Such modules should also set a `closePrompt` that
says what is lost on closing.

### Status without module code

`status.kind` tells the host how to determine the text below the description:

| `kind` | fields | result |
| --- | --- | --- |
| `none` | | no status |
| `process` | `process` | is a process of this name running |
| `path` | `path` | does this path exist (environment variables are expanded) |
| `count` | `path`, `pattern` | how many files are in it; `{n}` in the text is replaced |
| `tools` | | are all `requires` entries found |
| `cached` | | what the module last reported itself |

`whenTrue` and `whenFalse` set the text. Examples:

```json
"status": { "kind": "process", "process": "Discord",
            "whenTrue": "running", "whenFalse": "not started" }

"status": { "kind": "count", "path": "%USERPROFILE%\\Videos\\WinTo",
            "pattern": "*.mp4", "whenTrue": "{n} recordings", "whenFalse": "none yet" }
```

`cached` covers everything else. The module reports it:

```csharp
PublishStatus(StatusLevel.Good, "VB-CABLE detected");
```

**A status is a state, not a report.** It is cached and still shown days later, so it must
still be true then. "38 updates" is a state; "12 cleaned" is a report on a past run. Modules
without a lasting state use `"kind": "none"` and report only what is running right now via
`SetActivity`. A cached status older than 14 days isn't shown at all.

### External tools

```json
"requires": [
  { "tool": "yt-dlp", "hint": "winget install yt-dlp", "mandatory": true }
],
"status": { "kind": "tools", "whenTrue": "ready" }
```

In the module:

```csharp
var exe = Context.Tools.Find("yt-dlp");                // module folder, app\tools, PATH
var updatable = Context.Tools.FindWritable("yt-dlp");  // copy in the data folder
```

`FindWritable` is for programs that update themselves: an install under `Program Files` is
read-only for the user, and `yt-dlp -U` fails there.

With a `url`, the shell asks **before** opening and downloads the program into the data
folder. It verifies the file before treating it as a tool:

```json
{ "tool": "yt-dlp", "mandatory": true,
  "url": "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe",
  "checksumsUrl": "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS" }
```

`checksumsUrl` for "the latest version" (a `sha256sum`-style list), `sha256` for a fixed one.
Without either only HTTPS applies, which only says where the file came from.

## 3. The entry type

```csharp
using Avalonia.Controls;
using MultiTool.Abstractions;

namespace MultiTool.Modules.MyModule;

public sealed class MyModule : ModuleBase
{
    private MyModuleViewModel? _viewModel;

    protected override void OnInitialize()
    {
        // Keep it cheap: the user is waiting for the window.
        _viewModel = new MyModuleViewModel(Context);
    }

    protected override Control Build() =>
        new MyModuleView { DataContext = _viewModel };

    public override void ViewActivated(Control view) { /* start timers */ }

    public override void ViewDeactivated(Control view) { /* stop timers */ }

    public override void Dispose()
    {
        _viewModel?.Dispose();
        base.Dispose();
    }
}
```

Four things everyone gets wrong once:

**The view model lives on the module, not the view.** `Build()` runs on every open; state held
in the view is lost when the tab closes.

**Nothing slow in a constructor.** `OnInitialize` and every constructor in it run inside the
user's click. Separate querying from displaying, query via `Task.Run`, display after the
`await`:

```csharp
var data = await Task.Run(() => QueryExpensively());
Show(data);            // back on the UI thread
```

**`ViewDeactivated` releases nothing.** It only pauses what runs for display. Devices, threads
and hooks belong in `Dispose`. A tab switch looks exactly like closing to Avalonia, which is
why the two methods exist.

**`Dispose` is the only memory release there is.** A module's load context is never unloaded
(see [lessons learned](lessons-learned.md)). Whatever the module holds, it holds until the app exits unless
`Dispose` returns it.

## 4. The view

The base look comes from `MultiTool.UI`: `Classes="card"`, `"h1"`, `"h2"`, `"caption"`,
`"label"`, `"mono"`, `"muted"`, `"accent"`, `"ghost"`, `"icon"`, `"badge"`, `"search"`.
Colors and radii via `{DynamicResource}`, never hard-coded. The accent can be changed in the
settings (since 0.12.0): use `AccentBrush`, `AccentSoftBrush` and friends, and `OnAccentBrush`
for text on an accent background, which turns dark on a light accent.

Styles only this module needs go into its own `Styles.axaml`, included **from the view**:

```xml
<UserControl.Styles>
  <StyleInclude Source="avares://MultiTool.Modules.MyModule/Styles.axaml" />
</UserControl.Styles>
```

This works although the assembly is loaded at runtime. **The shell must never reference a
module's `avares://`**, or the DLL loads at startup.

**Resources** (icons, templates, anything with `x:Key`) need one more rule:

```xml
<UserControl.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceInclude Source="avares://MultiTool.Modules.MyModule/Styles/Icons.axaml" />
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</UserControl.Resources>
```

Include it in **every** view that uses it via `StaticResource`, not only the top one.
`StaticResource` resolves when the XAML loads, before a nested control has a parent. Styles
differ: they apply at runtime across the whole tree, so once at the top is enough.

### Four small things that cost time

**The code-behind stays empty.** Avalonia's name generator creates a field for every `x:Name`
and writes `InitializeComponent`. Writing either again gives `CS0102`:

```csharp
public partial class MyModuleView : UserControl
{
    public MyModuleView() => InitializeComponent();   // nothing more
}
```

**A binding on `.Count` does nothing.** Avalonia doesn't convert `int` to `bool` in a binding;
`IsVisible="{Binding !Log.Count}"` silently has no effect. Use an explicit property:

```csharp
public bool HasLog => Log.Count > 0;
// in the constructor:
Log.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLog));
```

**Drag and drop changed in Avalonia 12.** `e.Data` and `DataFormats.Files` are gone; use
`e.DataTransfer.Contains(DataFormat.File)` and `e.DataTransfer.TryGetFiles()`. Example:
`soundboard/Views/ModuleView.axaml.cs`.

**`Watermark` is now `PlaceholderText`.** Only an obsolete warning, but warnings are errors here.

## 5. What the host offers

```csharp
Context.DataDirectory        // %APPDATA%\WinTo\modules\<id>, the only place to write
Context.ModuleDirectory      // where the DLL is; read-only under Program Files
Context.Settings.Load<T>()   // own settings.json
Context.Settings.Save(value)
Context.Log.Info("…")        // %APPDATA%\WinTo\logs\<id>.log
Context.Tools.Find("ffmpeg")
Context.Shutdown             // the app is exiting

Shell.ConfirmAsync("Title", "Question", "Delete", danger: true)
Shell.PickFilesAsync("Choose files", true, new FilePattern("Videos", "mp4", "mkv"))
Shell.PickFolderAsync("Output folder")
Shell.Notify("Done.")
Shell.Announce(new Announcement("Screenshot in the clipboard", "1265 × 828")  // since 0.12.0
{
    ImagePath = path, OpenModuleId = "screenshot", OpenArgument = path,
})
Shell.RevealInExplorer(path)
Shell.RunElevatedAsync("uninstaller")
Shell.OpenModule("storage", "duplicates:" + folder)   // open another module and pass something
```

**`Settings.Save` is deferred and doesn't throw.** It serializes immediately and writes after
400 ms of quiet on a background thread, so a slider may save on every step. Everything is on
disk at the latest on release, and `Load` sees unwritten state too. Errors go to the module log.

**Receiving** what another module passes:

```csharp
public override void OpenWith(string argument) => _viewModel?.UseFolder(argument);
```

Called after `CreateView` and `ViewActivated`, so the view exists. If the module is already
open, only this call comes. The module defines what the text means.

The same argument can come from outside the app: `WinTo.exe --open <id> --with <argument>`
(0.13.0) opens the module and passes it, forwarded to a running instance if there is one. The
Explorer menu entries use this, for example `--open files --with "convert:C:\photo.jpg"`. Treat
the text as untrusted input: it may name a file that doesn't exist.

**`Announce` is for results of work that started outside the window**, such as a global
shortcut: a card at the top right with an optional thumbnail, shown even while no WinTo
window is visible. A click opens `OpenModuleId` with `OpenArgument`. Everything else uses
`Notify`. Needs `"minHostVersion": "0.12.0"`.

### Helpers in the contract

They live in `MultiTool.Abstractions` and so in the default context. Writing your own copy
reopens the traps they close.

```csharp
// Run a program and listen to it.
var result = await ExternalProcess.RunAsync(ffprobe, ["-v", "quiet", "-of", "json", path]);
if (!result.Ok) Log.Warn(result.Problem);

await ExternalProcess.StreamAsync(ytDlp, arguments,
    onOutput: line => Dispatcher.UIThread.Post(() => Show(line)),
    cancellationToken: Context.Shutdown);

Humanize.Bytes(1_474_560)      // "1.4 MB"
Humanize.Clock(duration)       // "1:04:12"
Humanize.Ago(time)             // "3 days ago"

RecycleBin.Delete(paths);      // returns null, or the reason

ProtectedPaths.IsProtected(folder)   // drive roots, Windows, known folders and above
ExternalProcess.TieToApplication(p)  // if WinTo dies, p dies with it
PowerPlans.All() / PowerPlans.Activate(id)  // returns null, or the reason
```

**Anything that deletes asks `ProtectedPaths` first.** Paths from the registry or a broad
selection aren't trustworthy: an installer may register `C:\Program Files` as its location.

**A process that runs until "stop" gets tied to the app.** Without a job object a child
process survives a WinTo crash; an ffmpeg recording then writes until the disk is full.
Not for installers: aborting those halfway is worse.

Three things `ExternalProcess` handles that are easy to miss:

- **Arguments go as a list, never as one string.** Windows splits the string again on the
  receiving side, and a file name with spaces becomes two arguments.
- **Progress lines split on `\r`, not only `\n`.** yt-dlp and ffmpeg overwrite their own line
  and send no `\n` for minutes.
- **`Process.Start` runs via `Task.Run`.** It is synchronous and can take hundreds of ms on a
  program's first start (antivirus). Before the first real `await` it would run on the UI
  thread: measured 479 ms instead of 41 for a view querying ffmpeg twice.

```csharp
// Since 0.11.0; needs "minHostVersion": "0.11.0".
using MultiTool.Abstractions.Bluetooth;
BluetoothHeadsets.Snapshot()                 // paired headsets: name, connected, battery
BluetoothHeadsets.SetConnected(headset, true)
```

Long-running work is reported to the shell. The text appears in the footer and on the tile,
also with the view closed, and while `IsBusy` holds the module is neither released nor the
app quit without asking:

```csharp
SetActivity(new ModuleActivity(true, "Downloading…", 0.42));
…
SetIdle();
```

**Closing asks only when something would be lost.** Being busy is one case. The other is a
result that exists only in memory, such as a search result that isn't saved anywhere. Report
that through `HasUnsavedWork` (since 0.11.0):

```csharp
public override bool HasUnsavedWork => _viewModel?.HasGroups == true;
```

Everything the module already wrote to disk is not unsaved work. Most modules leave it at the
default `false`, and closing them asks nothing.

### A tool made of sections (since 0.13.0)

Related tools are one module with a section bar (Capture: screenshot and recording). Each section
keeps its own view model and gets a `SectionContext`:

```csharp
var screenshot = new ScreenshotViewModel(new SectionContext(Context, "screenshot"));
var recorder = new RecorderViewModel(new SectionContext(Context, "recorder", publishesStatus: true));
```

The name is the section's settings key and its data folder (`modules\<name>`). For a section that
was a module before, use its former id: its settings and data carry over without migration code.
Only one section reports the tile status. The bar is `Border.sections` with `Button.section`
buttons from `MultiTool.UI`; keep both views in the tree and switch `IsVisible`, so running work
survives switching. Work a section only needs when shown goes into its first showing, not into
`Build`.

## 6. Build and check

```powershell
.\tools\build.ps1 -SelfTest
```

The self-test loads every module without a window, runs the full lifecycle and reports
assembly counts before and after. **Exit code 2 means a module was in memory before it was
opened**: somewhere the shell references the module.

It also checks the manifest icon (an unreadable path silently falls back to a placeholder) and
reports managed memory **after** release. Much more than a few MB means background work
outlives `Dispose`. Narrow it down with single modules:

```powershell
.\src\MultiTool.Host\bin\Release\net10.0-windows\WinTo.exe --selftest soundboard recorder
```

Then:

```powershell
.\tools\build.ps1 -Run
```
