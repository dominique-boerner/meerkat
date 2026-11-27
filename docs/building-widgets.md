# Building widgets

A widget is a class that implements `IOverlayWidget` from `Meerkat.Abstractions`. The shell places
its view on the overlay, draws the frame and header around it, and takes care of moving, resizing,
persisting the layout and isolating failures.

In the current version widgets are compiled into the application and registered through dependency
injection. The contracts live in their own assembly so that widgets can be loaded as plugins later
without changing them.

## 1. Create a project

Add a class library next to the existing widget and reference the abstractions:

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

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Meerkat.Abstractions;

public sealed class ClockWidget(IOverlayState overlayState) : IOverlayWidget
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TextBlock? _view;

    public string Id => "clock";

    public string Title => "Clock";

    public FrameworkElement View => _view ??= new TextBlock { FontSize = 28 };

    public Size DefaultSize => new(260, 140);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer.Tick += OnTick;
        _timer.Start();
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
        if (overlayState.IsVisible && _view is not null)
        {
            _view.Text = DateTime.Now.ToLongTimeString();
        }
    }
}
```

| Member | Purpose |
|---|---|
| `Id` | Stable identifier. Used as the key for the stored layout and settings, so do not change it after release. |
| `Title` | Shown in the widget header and in the settings. |
| `View` | The widget content. Created lazily on the UI thread and hosted exactly once; return the same instance on every call. |
| `HeaderView` | Optional content next to the title, for example a status text or buttons. |
| `DefaultSize` | Size in device-independent pixels used until the user resizes the widget. |
| `SettingsPage` | Optional settings editor, see [Settings](#settings). |
| `StartAsync` / `StopAsync` | Called when the widget is enabled, disabled or restarted, and on shutdown. Both run on the UI thread. |

## 3. Register the widget

Expose an extension method from your project and call it in `App.ConfigureServices` of `Meerkat.App`:

```csharp
public static class ClockWidgetServiceCollectionExtensions
{
    public static IServiceCollection AddClockWidget(this IServiceCollection services) =>
        services.AddSingleton<IOverlayWidget, ClockWidget>();
}
```

```csharp
services.AddDockerWidget();
services.AddClockWidget();
```

Add a project reference from `Meerkat.App` to the widget project and the widget appears on the
overlay and in the settings.

## Services provided by the shell

Request these through constructor injection.

| Service | Use it to |
|---|---|
| `IOverlayState` | Read whether the overlay is visible and which mode is active, and react to changes through `Changed`. |
| `IWidgetSettingsStore` | Load and save a settings object for your widget. It is stored as JSON in the user's settings file. |
| `IOverlayPanelHost` | Show an additional, independently movable panel such as a log viewer. Dispose the returned handle to close it. |
| `IOverlayCommands` | Ask the shell to open the settings window. |

## Behaving well on the overlay

- **Pause when hidden.** Do not poll or render while `IOverlayState.IsVisible` is `false`.
- **Respect the mode.** In display mode the overlay is click-through, so show actions only while
  `IOverlayState.Mode` is `OverlayMode.Interactive`.
- **Keep the UI thread free.** `StartAsync`, `StopAsync` and all view code run on the UI thread;
  move I/O and process calls to background work and return quickly.
- **Stop cleanly.** `StopAsync` should cancel background work and release resources. The shell stops
  waiting after a few seconds.
- **Fail loudly.** If `View` or `StartAsync` throws, the shell suspends the widget, logs the
  exception and offers a restart. Prefer that over swallowing errors you cannot handle.

## Settings

Keep settings in a plain class and persist it with `IWidgetSettingsStore`:

```csharp
public sealed class ClockSettings
{
    public bool ShowSeconds { get; set; } = true;
}

var settings = settingsStore.Load<ClockSettings>("clock");
settingsStore.Save("clock", settings);
```

To let users edit them, return an `IWidgetSettingsPage` from `SettingsPage`. The shell embeds the
view returned by `CreateView()` as a section of the settings window and calls `Apply()` when the
user saves. `CreateView()` is called every time the settings window opens and must return a fresh
view that reflects the stored values.

## Styling

`Meerkat.Abstractions` ships the theme used by the shell (`Themes/OverlayTheme.xaml`). Buttons, text
boxes, combo boxes, check boxes, sliders and scroll bars are styled automatically. Reference colors,
fonts and named styles with `DynamicResource` so your widget matches the rest of the overlay:

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

The widget frame, background and header are drawn by the shell; your view only provides the content.

## Reference implementation

`src/Meerkat.Widgets.Docker` is a complete widget that uses everything described here: a header
view, a settings page, a secondary panel for logs, polling that pauses while hidden, and view models
covered by unit tests.
