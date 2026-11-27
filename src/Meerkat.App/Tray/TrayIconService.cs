using System.Drawing.Drawing2D;
using Meerkat.Abstractions;
using Meerkat.App.Interop;
using Meerkat.App.Overlay;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Meerkat.App.Tray;

public sealed class TrayIconService : IDisposable
{
    private const int BalloonTimeoutMilliseconds = 5000;
    private const int IconSize = 32;

    private static readonly Drawing.Color Surface = Drawing.Color.FromArgb(0x1C, 0x1F, 0x26);
    private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x2B, 0x30, 0x3A);
    private static readonly Drawing.Color Text = Drawing.Color.FromArgb(0xE6, 0xE8, 0xEC);
    private static readonly Drawing.Color TextMuted = Drawing.Color.FromArgb(0x8B, 0x93, 0xA1);
    private static readonly Drawing.Color Accent = Drawing.Color.FromArgb(0x5B, 0xB6, 0xFF);

    private readonly OverlayState _state;
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _visibilityItem;
    private readonly WinForms.ToolStripMenuItem _modeItem;
    private readonly Drawing.Icon _icon;

    public TrayIconService(OverlayState state)
    {
        _state = state;

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
        _menu.Items.AddRange([_visibilityItem, _modeItem, new WinForms.ToolStripSeparator(), settingsItem, new WinForms.ToolStripSeparator(), exitItem]);

        _icon = CreateIcon();
        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _icon,
            Text = "Meerkat",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ToggleVisibilityRequested?.Invoke(this, EventArgs.Empty);

        _state.Changed += OnStateChanged;
        OnStateChanged(this, EventArgs.Empty);
    }

    public event EventHandler? ToggleVisibilityRequested;

    public event EventHandler? ToggleModeRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? ExitRequested;

    public void ShowWarning(string message) =>
        _notifyIcon.ShowBalloonTip(BalloonTimeoutMilliseconds, "Meerkat", message, WinForms.ToolTipIcon.Warning);

    public void Dispose()
    {
        _state.Changed -= OnStateChanged;

        // Hiding first removes the icon immediately instead of leaving a ghost until the tray is hovered.
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        NativeMethods.DestroyIcon(_icon.Handle);
        _icon.Dispose();
    }

    private static WinForms.ToolStripMenuItem CreateItem(Action onClick)
    {
        var item = new WinForms.ToolStripMenuItem { ForeColor = Text };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static Drawing.Icon CreateIcon()
    {
        using var bitmap = new Drawing.Bitmap(IconSize, IconSize);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var frame = new Drawing.Pen(Accent, 3);
            using var fill = new Drawing.SolidBrush(Accent);
            graphics.DrawRectangle(frame, 3, 6, 26, 20);
            graphics.FillRectangle(fill, 8, 11, 16, 3);
            graphics.FillRectangle(fill, 8, 17, 10, 3);
        }

        return Drawing.Icon.FromHandle(bitmap.GetHicon());
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
