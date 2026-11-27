using System.Windows;
using Meerkat.Abstractions;
using Meerkat.App.Interop;
using Meerkat.App.Overlay;
using Meerkat.App.Updates;
using Microsoft.Win32;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Meerkat.App.Tray;

public sealed class TrayIconService : IDisposable
{
    private const int BalloonTimeoutMilliseconds = 5000;
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string SystemUsesLightThemeValue = "SystemUsesLightTheme";

    private static readonly Drawing.Color Surface = Drawing.Color.FromArgb(0x1C, 0x1F, 0x26);
    private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x2B, 0x30, 0x3A);
    private static readonly Drawing.Color Text = Drawing.Color.FromArgb(0xE6, 0xE8, 0xEC);
    private static readonly Drawing.Color TextMuted = Drawing.Color.FromArgb(0x8B, 0x93, 0xA1);
    private static readonly Drawing.Color Accent = Drawing.Color.FromArgb(0x5B, 0xB6, 0xFF);

    private const float BadgeSizeRatio = 0.42f;

    private readonly OverlayState _state;
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _visibilityItem;
    private readonly WinForms.ToolStripMenuItem _modeItem;

    private readonly UpdateViewModel _update;
    private readonly WinForms.ToolStripMenuItem _updateItem;
    private readonly WinForms.ToolStripSeparator _updateSeparator = new();

    // Named after the icon's own color: the light icon is for a dark taskbar and vice versa.
    private readonly Drawing.Icon _lightIcon = LoadIcon("meerkat-tray-light.ico");
    private readonly Drawing.Icon _darkIcon = LoadIcon("meerkat-tray-dark.ico");
    private readonly Drawing.Icon _lightBadgedIcon;
    private readonly Drawing.Icon _darkBadgedIcon;

    public TrayIconService(OverlayState state, UpdateViewModel update)
    {
        _state = state;
        _update = update;
        _lightBadgedIcon = AddBadge(_lightIcon);
        _darkBadgedIcon = AddBadge(_darkIcon);

        _updateItem = CreateItem(_update.RunTrayAction);
        _updateItem.ForeColor = Accent;
        _updateItem.Font = new Drawing.Font(_updateItem.Font, Drawing.FontStyle.Bold);

        _visibilityItem = CreateItem(() => ToggleVisibilityRequested?.Invoke(this, EventArgs.Empty));
        _modeItem = CreateItem(() => ToggleModeRequested?.Invoke(this, EventArgs.Empty));
        var settingsItem = CreateItem(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        settingsItem.Text = "Settings …";
        var exitItem = CreateItem(() => ExitRequested?.Invoke(this, EventArgs.Empty));
        exitItem.Text = "Exit";

        _menu = new WinForms.ContextMenuStrip
        {
            Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkColorTable()),
            ShowImageMargin = false,
            BackColor = Surface,
            ForeColor = Text,
        };
        _menu.Items.Add(new WinForms.ToolStripLabel("MEERKAT")
        {
            ForeColor = TextMuted,
            Font = new Drawing.Font(_menu.Font.FontFamily, 7.5f, Drawing.FontStyle.Bold),
        });
        _menu.Items.AddRange([_updateItem, _updateSeparator, _visibilityItem, _modeItem, new WinForms.ToolStripSeparator(), settingsItem, new WinForms.ToolStripSeparator(), exitItem]);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = SelectIcon(),
            Text = "Meerkat",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ToggleVisibilityRequested?.Invoke(this, EventArgs.Empty);

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _state.Changed += OnStateChanged;
        _update.PropertyChanged += OnUpdateChanged;
        OnStateChanged(this, EventArgs.Empty);
        OnUpdateChanged(this, EventArgs.Empty);
    }

    public event EventHandler? ToggleVisibilityRequested;

    public event EventHandler? ToggleModeRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? ExitRequested;

    public void ShowWarning(string message) =>
        _notifyIcon.ShowBalloonTip(BalloonTimeoutMilliseconds, "Meerkat", message, WinForms.ToolTipIcon.Warning);

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _state.Changed -= OnStateChanged;
        _update.PropertyChanged -= OnUpdateChanged;

        // Hiding first removes the icon immediately instead of leaving a ghost until the tray is hovered.
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _lightIcon.Dispose();
        _darkIcon.Dispose();

        // Icons created from a bitmap handle do not own it, so it has to be released explicitly.
        foreach (var badgedIcon in new[] { _lightBadgedIcon, _darkBadgedIcon })
        {
            NativeMethods.DestroyIcon(badgedIcon.Handle);
            badgedIcon.Dispose();
        }
    }

    /// <summary>Draws the accent dot that marks a pending update onto a copy of the icon.</summary>
    private static Drawing.Icon AddBadge(Drawing.Icon icon)
    {
        using var bitmap = icon.ToBitmap();
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        using (var brush = new Drawing.SolidBrush(Accent))
        {
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var diameter = bitmap.Width * BadgeSizeRatio;
            graphics.FillEllipse(brush, bitmap.Width - diameter, 0, diameter, diameter);
        }

        return Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    private static WinForms.ToolStripMenuItem CreateItem(Action onClick)
    {
        var item = new WinForms.ToolStripMenuItem { ForeColor = Text };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static Drawing.Icon LoadIcon(string fileName)
    {
        var resource = Application.GetResourceStream(new Uri($"Assets/{fileName}", UriKind.Relative))
            ?? throw new InvalidOperationException($"Icon resource '{fileName}' is missing.");
        using var stream = resource.Stream;
        return new Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
    }

    private static bool IsTaskbarLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue(SystemUsesLightThemeValue) is 1;
    }

    private Drawing.Icon SelectIcon() => (IsTaskbarLight(), _update.HasUpdate) switch
    {
        (true, true) => _darkBadgedIcon,
        (true, false) => _darkIcon,
        (false, true) => _lightBadgedIcon,
        (false, false) => _lightIcon,
    };

    private void OnUpdateChanged(object? sender, EventArgs e)
    {
        var actionText = _update.TrayActionText;
        _updateItem.Text = actionText ?? string.Empty;
        _updateItem.Visible = actionText is not null;
        _updateItem.Enabled = _update.IsReady || _update.IsAvailable;
        _updateSeparator.Visible = actionText is not null;
        _notifyIcon.Icon = SelectIcon();
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Switching between the light and dark Windows theme is reported in the General category.
        if (e.Category == UserPreferenceCategory.General)
        {
            _notifyIcon.Icon = SelectIcon();
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        _visibilityItem.Text = _state.IsVisible ? "Hide overlay" : "Show overlay";
        _visibilityItem.ShortcutKeyDisplayString = _state.VisibilityHotkeyText;
        _modeItem.Text = _state.Mode == OverlayMode.Interactive ? "Display mode" : "Interactive mode";
        _modeItem.ShortcutKeyDisplayString = _state.ModeHotkeyText;
    }

    private sealed class DarkColorTable : WinForms.ProfessionalColorTable
    {
        public override Drawing.Color ToolStripDropDownBackground => Surface;

        public override Drawing.Color MenuBorder => Hover;

        public override Drawing.Color MenuItemBorder => Hover;

        public override Drawing.Color MenuItemSelected => Hover;

        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;

        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;

        public override Drawing.Color SeparatorDark => Hover;

        public override Drawing.Color SeparatorLight => Hover;

        public override Drawing.Color ImageMarginGradientBegin => Surface;

        public override Drawing.Color ImageMarginGradientMiddle => Surface;

        public override Drawing.Color ImageMarginGradientEnd => Surface;
    }
}
