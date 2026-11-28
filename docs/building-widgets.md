# Building widgets

A widget is a class that implements `IOverlayWidget` from `Meerkat.Abstractions`. It contributes two
things to the shell:

- a **status** and **alerts** for the status bar, which is all the user sees while everything works
- a **page** in the panel, where the user looks at details and acts on them

The shell draws the status bar and the panel, decides what is prominent enough to show, and isolates
failures.

A widget reaches Meerkat in one of two ways, and is written the same for both:

- as a **plugin**: a project of your own, in a repository of your own, whose build output is copied
  into the plugins folder of an unchanged Meerkat. This is the way for a widget that is of use to
  you or your company only. See [Shipping a widget as a plugin](#shipping-a-widget-as-a-plugin).
- **built in**: a project in this repository that is compiled into the application. This is the way
  for a widget that is meant to become part of Meerkat. Steps 1 to 3 describe it.

## 1. Create a project

Add a class library next to the existing widgets and reference the abstractions:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Meerkat.Abstractions\Meerkat.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

The target framework and code analysis settings are inherited from `Directory.Build.props`.

## 2. Implement `IOverlayWidget`

This widget watches the free space of a drive. It reports a status for the status bar, raises an alert
when space runs low, and shows a line of text as its page.

```csharp
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Meerkat.Abstractions;

public sealed class DiskWidget(IOverlayState overlayState) : IOverlayWidget
{
    private const double WarningThresholdGigabytes = 20;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private TextBlock? _view;
    private DateTimeOffset? _lowSince;

    public event EventHandler? StatusChanged;

    public string Id => "disk";

    public string Title => "Disk";

    public string Icon => "";

    public FrameworkElement View => _view ??= new TextBlock();

    public WidgetStatus? Status { get; private set; }

    public IReadOnlyList<WidgetAlert> Alerts { get; private set; } = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer.Tick += OnTick;
        _timer.Start();
        Refresh();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        return Task.CompletedTask;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Skip work nobody can see.
        if (overlayState.IsVisible)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        var freeGigabytes = new DriveInfo("C").AvailableFreeSpace / 1e9;
        var isLow = freeGigabytes < WarningThresholdGigabytes;
        _lowSince = isLow ? _lowSince ?? DateTimeOffset.Now : null;

        Status = new WidgetStatus("Disk", $"{freeGigabytes:0} GB", isLow ? WidgetSeverity.Warning : WidgetSeverity.Ok);
        Alerts = isLow
            ? [new WidgetAlert(WidgetSeverity.Warning, "Drive C: is almost full", $"{freeGigabytes:0} GB free", _lowSince!.Value)]
            : [];
        if (_view is not null)
        {
            _view.Text = $"{freeGigabytes:0.0} GB free on C:";
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
```

| Member | Purpose |
|---|---|
| `Id` | Stable identifier. Used as the key for the stored settings, so do not change it after release. |
| `Title` | Shown below the widget's icon in the panel and in the settings. |
| `Icon` | A glyph from the Segoe Fluent Icons font for the panel. |
| `View` | The widget's page in the panel. Created lazily on the UI thread; return the same instance on every call. |
| `HeaderView` | Optional content above the page, for example a summary and buttons. |
| `Status` | The widget's chip in the status bar: a label, a compact figure and a severity. Return null while there is nothing to report. |
| `AdditionalStatuses` | Further chips, shown right after `Status`. For the rare widget that watches two things worth telling apart at a glance, as the Docker widget does with containers and images. |
| `Alerts` | Conditions that need attention right now. An alert may bring an `Icon` that tells what it is about; otherwise its card shows the icon of the widget. Keep the title stable while the alert says the same thing: a card the user put away returns when the title changes. |
| `StatusCard` | Optional card below the status bar for something the user chose to keep in view, see [Status cards and hotkeys](#status-cards-and-hotkeys). |
| `Hotkeys` | Optional global hotkeys, see [Status cards and hotkeys](#status-cards-and-hotkeys). |
| `Actions` | Optional things the widget does on request, see [Actions](#actions). |
| `StatusChanged` | Raise it whenever `Status`, `AdditionalStatuses`, `Alerts` or `StatusCard` changed. It may be raised from any thread. |
| `SettingsPage` | Optional settings editor, see [Settings](#settings). |
| `StartAsync` / `StopAsync` | Called when the widget is enabled, disabled or restarted, and on shutdown. Both run on the UI thread. |

## 3. Register the widget

Expose an extension method from your project and call it in `App.ConfigureServices` of `Meerkat.App`:

```csharp
public static class DiskWidgetServiceCollectionExtensions
{
    public static IServiceCollection AddDiskWidget(this IServiceCollection services) =>
        services.AddSingleton<IOverlayWidget, DiskWidget>();
}
```

```csharp
services.AddDockerWidget();
services.AddGitWidget();
services.AddDiskWidget();
```

Add a project reference from `Meerkat.App` to the widget project and the widget appears in the status
bar, the panel and the settings.

## Shipping a widget as a plugin

A plugin is a folder that holds an assembly of the same name:

```
%LocalAppData%\Meerkat\plugins\
  Acme.Deploys\
    Acme.Deploys.dll      every public class in it that implements IOverlayWidget becomes a widget
    Some.Library.dll      whatever else the plugin needs
```

Meerkat looks into that folder when it starts, and into a `plugins` folder next to `Meerkat.exe`,
where an administrator can install plugins for everybody who uses the computer. Updates of Meerkat
leave both alone. *Open plugins folder* on the *General* page of the settings leads there; the page
also lists the plugins that were found, and says why if one does not work.

### The project

The contracts are published as packages with every
[release](https://github.com/dominique-boerner/meerkat/releases): `Meerkat.Abstractions`, and
`Meerkat.Ssh` for widgets that reach other machines. Download them into a folder and name it as a
package source in a `nuget.config` next to your project:

```xml
<configuration>
  <packageSources>
    <add key="meerkat" value="packages" />
  </packageSources>
</configuration>
```

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <!-- Copies the libraries the plugin uses next to it, where Meerkat looks for them. -->
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- Meerkat brings the contracts itself, so they are built against but not copied. -->
    <PackageReference Include="Meerkat.Abstractions" Version="1.1.0" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

Name the assembly like the folder it will be installed into. There is nothing to register: the
widget from step 2 works as it is. Its constructor may ask for the
[services provided by the shell](#services-provided-by-the-shell), and for `ISshClient` and
`MachineRegistry` when the project references `Meerkat.Ssh`. Build the project, copy the output into
a folder below the plugins folder, and restart Meerkat.

`samples/Meerkat.Plugins.Sample` is such a plugin, with a page written in XAML. It references the
project instead of the package only because it lives in this repository.

### What is shared and what is not

- Whatever Meerkat brings itself is shared with the plugin: the contracts, WPF, and the libraries
  Meerkat uses. A copy of one of them in the plugin's folder is ignored.
- Everything else is loaded from the plugin's folder and belongs to that plugin alone, so two plugins
  may bring different versions of the same library.
- The id of a widget must not be taken by another widget; prefix it with something of your own, such
  as `acme-deploys`.
- A loaded plugin keeps its files open. To replace one, exit Meerkat first.

### Versions

The packages carry the version of the contract, not of Meerkat, and it only moves when the contract
does. Meerkat loads a plugin built against the same major version and a minor version up to its own:

| Built against | Meerkat brings | |
|---|---|---|
| 1.0 | 1.3 | Loaded: a minor version only adds |
| 1.4 | 1.3 | Refused: the plugin may use what this Meerkat lacks; update Meerkat |
| 1.x | 2.0 | Refused: something a plugin may use changed; rebuild the plugin against 2.0 |

### Trust

A plugin is code that runs as part of Meerkat with all rights of the user: it can read files, reach
the network and read what other widgets stored in the Credential Manager. Meerkat only loads what
lies in the two folders and never fetches a plugin itself. Install only plugins whose source you
trust.

## Status and alerts

The status bar is always visible, so the shell is strict about what reaches it:

| Severity | Status bar | Panel |
|---|---|---|
| `Ok` | Green chip | No badge |
| `Warning` | Yellow chip; the alert is only counted ("+2 more hints"), unless it is marked `Prominent`, which gives it a yellow card | Yellow badge on the widget's tab |
| `Error` | Red chip and a card with title, detail and age | Red badge on the widget's tab |

Choose severities with that in mind:

- Use `Error` for things that are broken and that the user would want to know about immediately, such
  as a failed pipeline or an unreachable service.
- Use `Warning` for things worth knowing that can wait, such as a review request.
- Mark a warning as `Prominent` only if it explains trouble the user is about to run into elsewhere,
  such as a disturbance of a service they depend on. It then gets a card like an error does.
- Do not raise an alert for a state the user chose, such as a container they stopped, or for a widget
  that is simply not set up yet. Return a null `Status` in the latter case.
- Set `Since` to the moment the condition was first observed and keep it stable across refreshes; the
  shell shows it as the alert's age and uses it to order cards.
- Show the same problem on your page, with the actions to resolve it. A click on the card only opens
  your page; it cannot act on the problem.

## Status cards and hotkeys

A widget can keep something in view below the status bar by returning a `WidgetStatusCard`: a title
and a few lines, each optionally struck through. Only do this on the user's request, for example
behind a pin button, and return null otherwise. A click on the card opens your page, so a card can be
read but not operated.

A widget can also offer global hotkeys:

```csharp
public IReadOnlyList<WidgetHotkey> Hotkeys =>
[
    new("quick-note", "Quick note", "Ctrl+Alt+N", ShowCaptureWindow),
];
```

The shell registers them while the widget is enabled, lists them on the *General* page of the
settings so the user can change the combination, and calls the handler on the UI thread. The id
identifies the hotkey in the stored settings, so keep it stable. A hotkey should do something that is
worth interrupting the user's work for, such as opening a window of your own for a quick entry.

## Actions

The shell has a search for actions that opens above any window with a global hotkey. A widget adds
to it by returning `WidgetAction`s, so that the user can act without opening the panel first:

```csharp
public IReadOnlyList<WidgetAction> Actions =>
[
    new()
    {
        Id = "empty-recycle-bin",
        Title = "Empty recycle bin",
        Hint = "1.2 GB",
        Confirm = _ => new WidgetActionConfirmation("Empty the recycle bin?", "This cannot be undone.", "Empty"),
        Run = async (_, cancellationToken) =>
        {
            await EmptyAsync(cancellationToken);
            return WidgetActionResult.Success("The recycle bin is empty");
        },
    },
];
```

`Actions` is read on the UI thread every time the search opens, so the list and its hints may follow
the state of the widget; leave out what is not possible right now, and keep the getter cheap.

- **Targets.** An action that applies to one of several things returns them from `Targets`, in
  groups; the search then asks for one in a second step and hands it to `Run`. A target the action
  cannot be applied to is listed with the reason in `Unavailable`, so that nobody looks for it in
  vain, but cannot be chosen. Identify targets by an id and look them up again in `Run`: time has
  passed since they were listed.
- **Confirmation.** `Confirm` may return a question for what cannot be undone and for what others
  would notice, such as a change on another machine. Do not ask for everything.
- **Result.** `WidgetActionResult.Done` closes the search without a word, for an action whose result
  shows itself, such as a page that opens. `Success` says a sentence that is shown for a moment.
  `Failure` stays until it was read and offers to open the page of the widget, so show the problem
  there too. Await what the action does and report how it really ended, not that it was started.
- **Free text.** One action may set `RunWithText`; it is offered what the user typed when nothing
  matched it. The Notes widget saves such text as a quick note.
- To show the result on your page, ask `IOverlayCommands.ShowWidget` for it.

Write titles as requests ("Restart container") and keep the id stable; the actions used last are
remembered under it.

## Services provided by the shell

Request these through constructor injection.

| Service | Use it to |
|---|---|
| `IOverlayState` | Read whether Meerkat is visible and whether the panel is open, and react to changes through `Changed`. |
| `IWidgetSettingsStore` | Load and save a settings object for your widget. It is stored as JSON in the user's settings file. |
| `IWidgetSecretStore` | Keep secrets such as access tokens in the Windows Credential Manager instead of the settings file. |
| `IOverlayCommands` | Ask the shell to open the settings window, a web address in the browser or the page of a widget in the panel. |

A widget that needs to reach other machines can reference the `Meerkat.Ssh` project and request
`ISshClient`. It runs commands through the OpenSSH client of Windows, so the user's SSH configuration,
agent and `known_hosts` apply, and it never accepts a host key on its own while polling.

## Behaving well

- **Keep reporting while the panel is closed.** The status bar depends on it. Only stop polling while
  `IOverlayState.IsVisible` is `false`, which means the user has hidden Meerkat.
- **Keep the UI thread free.** `StartAsync`, `StopAsync` and all view code run on the UI thread;
  move I/O and process calls to background work and return quickly.
- **Stop cleanly.** `StopAsync` should cancel background work and release resources. The shell stops
  waiting after a few seconds.
- **Fail loudly.** If `View` or `StartAsync` throws, the shell suspends the widget, logs the
  exception, reports it in the status bar and offers a restart. Prefer that over swallowing errors
  you cannot handle.

## Settings

Keep settings in a plain class and persist it with `IWidgetSettingsStore`:

```csharp
public sealed class DiskSettings
{
    public string Drive { get; set; } = "C";
}

var settings = settingsStore.Load<DiskSettings>("disk");
settingsStore.Save("disk", settings);
```

To let users edit them, return an `IWidgetSettingsPage` from `SettingsPage`. The shell embeds the
view returned by `CreateView()` as the widget's page of the settings window, next to the switch that
turns the widget on and off. `CreateView()` is called every time the settings window opens and must
return a fresh view that reflects the stored values.

The settings window has no save button, so a page stores every change itself:

- Save as soon as a value is complete and valid, and let the widget follow at once. A choice or a
  check box is complete when it changes; a text field when it loses the focus or holds a valid value.
- Do not store what is not valid yet. Say next to the field what is wrong and keep the value stored
  last until it is right.
- Something that takes several fields, such as a new entry of a list, is a draft until the user
  confirms it; store the list then.
- Keep explanations out of the page. Put them into the tooltip of a small info symbol next to the
  label: `<ContentControl Style="{DynamicResource Overlay.Info}" ToolTip="…" />`.

The settings file can also be edited by hand while Meerkat runs. `IWidgetSettingsStore.Reloaded` is
raised when that happened; load your settings again then instead of keeping the object you had.

A widget that reaches other machines over SSH does not keep their addresses itself. Request
`MachineRegistry` from `Meerkat.Ssh`, store only the ids of the machines you use together with what
you do with them, and let the user choose among the machines on your page.

## Styling

`Meerkat.Abstractions` ships the theme used by the shell (`Themes/OverlayTheme.xaml`). Buttons, text
boxes, combo boxes, check boxes, sliders and scroll bars are styled automatically. Reference colors,
fonts and named styles with `DynamicResource` so your widget matches the rest of Meerkat:

```xml
<TextBlock Text="12 running"
           Foreground="{DynamicResource Overlay.TextMuted}"
           FontFamily="{DynamicResource Overlay.MonoFont}" />
<Button Style="{DynamicResource Overlay.IconButton}" Content="&#xE72C;" ToolTip="Refresh" />
```

Commonly used keys:

| Kind | Keys |
|---|---|
| Brushes | `Overlay.Text`, `Overlay.TextMuted`, `Overlay.Accent`, `Overlay.Success`, `Overlay.Warning`, `Overlay.Danger`, `Overlay.SurfaceRaised`, `Overlay.Border`, `Overlay.Hover` |
| Fonts | `Overlay.MonoFont`, `Overlay.IconFont` |
| Styles | `Overlay.Button`, `Overlay.PrimaryButton`, `Overlay.IconButton`, `Overlay.Chip`, `Overlay.Meter`, `Overlay.ColumnHeader`, `Overlay.Hint`, `Overlay.FormLabel` |

The panel provides the background; your view only provides the content. The page is about 560
device-independent pixels wide.

## Reference implementations

`src/Meerkat.Widgets.Docker` is a complete widget that uses most of what is described here: status
and alerts, a header view, a settings page, an embedded log viewer, polling that pauses while hidden,
and view models covered by unit tests.

`src/Meerkat.Widgets.Git` shows how to talk to a web API: two providers behind one interface, a token
kept in the secret store, error states for expired tokens and rate limits, and tests that replace the
HTTP layer with canned responses.

`src/Meerkat.Widgets.Server` monitors several independent targets: one polling loop per host with its
own interval, alerts that keep the time a problem started, a list that sorts itself by severity, and
a settings page with a list and an edit form.

`src/Meerkat.Widgets.Services` polls web addresses that belong to others: one request per check with
a floor for the interval, a failed check that is kept apart from a bad state, prominent warnings,
and a settings form with a test request.

`src/Meerkat.Widgets.Notes` works without any polling: it keeps its data in files, watches them for
changes made elsewhere, offers global hotkeys with a window of its own, and pins a card below the
status bar.
