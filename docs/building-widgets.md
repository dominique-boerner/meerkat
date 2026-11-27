# Building widgets

A widget is a class that implements `IOverlayWidget` from `Meerkat.Abstractions`. It contributes two
things to the shell:

- a **status** and **alerts** for the status bar, which is all the user sees while everything works
- a **page** in the panel, where the user looks at details and acts on them

The shell draws the status bar and the panel, decides what is prominent enough to show, and isolates
failures. In the current version widgets are compiled into the application and registered through
dependency injection. The contracts live in their own assembly so that widgets can be loaded as
plugins later without changing them.

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
| `Alerts` | Conditions that need attention right now. |
| `StatusChanged` | Raise it whenever `Status` or `Alerts` changed. It may be raised from any thread. |
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

## Status and alerts

The status bar is always visible, so the shell is strict about what reaches it:

| Severity | Status bar | Panel |
|---|---|---|
| `Ok` | Green chip | No badge |
| `Warning` | Yellow chip; the alert is only counted ("+2 more hints") | Yellow badge on the widget's tab |
| `Error` | Red chip and a card with title, detail and age | Red badge on the widget's tab |

Choose severities with that in mind:

- Use `Error` for things that are broken and that the user would want to know about immediately, such
  as a failed pipeline or an unreachable service.
- Use `Warning` for things worth knowing that can wait, such as a review request.
- Do not raise an alert for a state the user chose, such as a container they stopped, or for a widget
  that is simply not set up yet. Return a null `Status` in the latter case.
- Set `Since` to the moment the condition was first observed and keep it stable across refreshes; the
  shell shows it as the alert's age and uses it to order cards.
- Show the same problem on your page, with the actions to resolve it. The status bar cannot be clicked.

## Services provided by the shell

Request these through constructor injection.

| Service | Use it to |
|---|---|
| `IOverlayState` | Read whether Meerkat is visible and whether the panel is open, and react to changes through `Changed`. |
| `IWidgetSettingsStore` | Load and save a settings object for your widget. It is stored as JSON in the user's settings file. |
| `IWidgetSecretStore` | Keep secrets such as access tokens in the Windows Credential Manager instead of the settings file. |
| `IOverlayCommands` | Ask the shell to open the settings window or a web address in the browser. |

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
view returned by `CreateView()` as a page of the settings window and calls `Apply()` when the user
saves. `CreateView()` is called every time the settings window opens and must return a fresh view
that reflects the stored values.

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
