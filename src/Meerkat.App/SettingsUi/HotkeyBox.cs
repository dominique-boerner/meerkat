using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Meerkat.App.Hotkeys;

namespace Meerkat.App.SettingsUi;

/// <summary>
/// A field that records a key combination instead of accepting typed text: click it, press the
/// combination, done. Escape or moving the focus away keeps the previous value.
/// </summary>
public sealed class HotkeyBox : Control
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey),
        typeof(string),
        typeof(HotkeyBox),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (sender, _) => ((HotkeyBox)sender).UpdateDisplayText()));

    private static readonly DependencyPropertyKey IsRecordingPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsRecording),
        typeof(bool),
        typeof(HotkeyBox),
        new PropertyMetadata(false));

    private static readonly DependencyPropertyKey DisplayTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(DisplayText),
        typeof(string),
        typeof(HotkeyBox),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsRecordingProperty = IsRecordingPropertyKey.DependencyProperty;

    public static readonly DependencyProperty DisplayTextProperty = DisplayTextPropertyKey.DependencyProperty;

    private const string RecordingPrompt = "Press a combination …";
    private const string EmptyText = "Not set";

    static HotkeyBox()
    {
        FocusableProperty.OverrideMetadata(typeof(HotkeyBox), new FrameworkPropertyMetadata(true));
    }

    /// <summary>Raised when recording starts; the application's own hotkeys must be released meanwhile.</summary>
    public event EventHandler? RecordingStarted;

    public event EventHandler? RecordingStopped;

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public bool IsRecording => (bool)GetValue(IsRecordingProperty);

    /// <summary>What the field shows: the hotkey, or feedback on the combination being pressed.</summary>
    public string DisplayText => (string)GetValue(DisplayTextProperty);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        Focus();
        StartRecording();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        // Alt combinations arrive as Key.System with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (!IsRecording)
        {
            // Keyboard users start recording the way they would press a button.
            if (key is Key.Enter or Key.Space && modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                StartRecording();
            }

            return;
        }

        if (key == Key.Tab && modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            // Let the focus move on; losing it ends the recording.
            return;
        }

        e.Handled = true;
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            StopRecording();
        }
        else if (HotkeyGesture.TryCreate(key, modifiers, out var gesture))
        {
            Hotkey = gesture.ToString();
            StopRecording();
        }
        else
        {
            UpdateDisplayText();
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (IsRecording)
        {
            // Releasing a modifier changes what is still being held.
            e.Handled = true;
            UpdateDisplayText();
        }
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        StopRecording();
    }

    private void StartRecording()
    {
        if (IsRecording)
        {
            return;
        }

        SetValue(IsRecordingPropertyKey, true);
        UpdateDisplayText();
        RecordingStarted?.Invoke(this, EventArgs.Empty);
    }

    private void StopRecording()
    {
        if (!IsRecording)
        {
            return;
        }

        SetValue(IsRecordingPropertyKey, false);
        UpdateDisplayText();
        RecordingStopped?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateDisplayText()
    {
        string text;
        if (!IsRecording)
        {
            text = string.IsNullOrEmpty(Hotkey) ? EmptyText : Hotkey;
        }
        else
        {
            // Echo the modifiers already held, so it is obvious that the field is listening.
            var held = HotkeyGesture.FormatModifiers(Keyboard.Modifiers);
            text = held.Length > 0 ? $"{held}+…" : RecordingPrompt;
        }

        SetValue(DisplayTextPropertyKey, text);
    }
}
