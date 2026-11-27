using System.Windows.Input;
using Meerkat.App.Hotkeys;

namespace Meerkat.Tests.App;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+O", ModifierKeys.Control | ModifierKeys.Alt, Key.O)]
    [InlineData("ctrl + shift + f12", ModifierKeys.Control | ModifierKeys.Shift, Key.F12)]
    [InlineData("Win+5", ModifierKeys.Windows, Key.D5)]
    public void TryParse_AcceptsModifiersAndKey(string text, ModifierKeys modifiers, Key key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(new HotkeyGesture(modifiers, key), gesture);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("O")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Nope")]
    [InlineData("Hyper+O")]
    [InlineData("Ctrl+42")]
    public void TryParse_RejectsIncompleteOrUnknownGestures(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
    }

    [Theory]
    [InlineData(Key.M, ModifierKeys.Control | ModifierKeys.Alt, true)]
    [InlineData(Key.F9, ModifierKeys.Shift, true)]
    [InlineData(Key.M, ModifierKeys.None, false)]
    [InlineData(Key.LeftCtrl, ModifierKeys.Control, false)]
    [InlineData(Key.RWin, ModifierKeys.Windows | ModifierKeys.Alt, false)]
    [InlineData(Key.None, ModifierKeys.Control, false)]
    public void TryCreate_NeedsAModifierAndARealKey(Key key, ModifierKeys modifiers, bool expected)
    {
        Assert.Equal(expected, HotkeyGesture.TryCreate(key, modifiers, out var gesture));
        if (expected)
        {
            Assert.Equal(new HotkeyGesture(modifiers, key), gesture);
        }
    }

    [Fact]
    public void FormatModifiers_ListsHeldModifiersInAFixedOrder()
    {
        Assert.Equal("Ctrl+Alt+Shift+Win", HotkeyGesture.FormatModifiers(ModifierKeys.Windows | ModifierKeys.Shift | ModifierKeys.Alt | ModifierKeys.Control));
        Assert.Equal("Alt", HotkeyGesture.FormatModifiers(ModifierKeys.Alt));
        Assert.Equal(string.Empty, HotkeyGesture.FormatModifiers(ModifierKeys.None));
    }

    [Fact]
    public void ToString_RoundTripsThroughTryParse()
    {
        var gesture = new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Windows, Key.D7);

        Assert.Equal("Ctrl+Win+7", gesture.ToString());
        Assert.Equal("Ctrl+Alt+O", HotkeyGesture.ToDisplayText("control + alt + o"));
        Assert.Equal("not a hotkey", HotkeyGesture.ToDisplayText("not a hotkey"));
        Assert.True(HotkeyGesture.TryParse(gesture.ToString(), out var parsed));
        Assert.Equal(gesture, parsed);
    }
}
