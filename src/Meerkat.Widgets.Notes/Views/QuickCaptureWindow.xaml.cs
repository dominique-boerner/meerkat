using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Meerkat.Widgets.Notes.Scratchpad;

namespace Meerkat.Widgets.Notes.Views;

/// <summary>
/// A single input line that appears above whatever the user is working on, takes a note or a task and
/// disappears again.
/// </summary>
public partial class QuickCaptureWindow : Window
{
    private readonly Func<string, CaptureKind, bool> _save;
    private CaptureKind _kind;
    private bool _isClosing;

    /// <param name="save">Saves the input; returns false if it could not be saved, which keeps the window open.</param>
    public QuickCaptureWindow(CaptureKind kind, Func<string, CaptureKind, bool> save)
    {
        _kind = kind;
        _save = save;
        InitializeComponent();
        UpdateHint();
        Loaded += (_, _) =>
        {
            Activate();
            Input.Focus();
        };
        Closing += (_, _) => _isClosing = true;
    }

    /// <summary>Switches an open window to the other kind of entry, keeping what was typed so far.</summary>
    public void Show(CaptureKind kind)
    {
        _kind = kind;
        UpdateHint();
        Activate();
        Input.Focus();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // The field belongs to the moment it was called up; left behind it would only cover the screen.
        CloseOnce();
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e) => UpdateHint();

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseOnce();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (string.IsNullOrWhiteSpace(Input.Text) || _save(Input.Text, _kind))
            {
                CloseOnce();
            }
            else
            {
                Hint.Text = "Could not be saved – see the Notes page in the panel";
                Hint.SetResourceReference(ForegroundProperty, "Overlay.Danger");
            }
        }
    }

    private void UpdateHint()
    {
        // A prefix such as "[]" turns a quick note into a task; the hint shows where the entry will go.
        var kind = QuickCapture.Parse(Input.Text, _kind)?.Kind ?? _kind;
        Hint.SetResourceReference(ForegroundProperty, "Overlay.TextMuted");
        Hint.Text = kind == CaptureKind.Task
            ? "→ Tasks"
            : "→ Quick notes · start with [] for a task";
        Glyph.Text = kind == CaptureKind.Task ? "" : "";
    }

    private void CloseOnce()
    {
        if (!_isClosing)
        {
            _isClosing = true;
            Close();
        }
    }
}
