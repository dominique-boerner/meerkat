using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Meerkat.Widgets.Notes.ViewModels;

namespace Meerkat.Widgets.Notes.Views;

public partial class NotesWidgetView : UserControl
{
    public NotesWidgetView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is NotesWidgetViewModel previous)
            {
                previous.TitleEditRequested -= OnTitleEditRequested;
            }

            if (e.NewValue is NotesWidgetViewModel current)
            {
                current.TitleEditRequested += OnTitleEditRequested;
            }
        };
    }

    private NotesWidgetViewModel? ViewModel => DataContext as NotesWidgetViewModel;

    private bool _nextCaretAtStart;

    // Clicking a block does not move the keyboard focus, so the block being edited would stay open.
    // Any click outside of its text field therefore ends the editing first.
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox { DataContext: BlockViewModel { IsEditing: true }, IsMouseOver: false })
        {
            ViewModel?.CommitEditing();
        }
    }

    private void OnBlockClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BlockViewModel block })
        {
            ViewModel?.BeginEdit(block);
            e.Handled = true;
        }
    }

    private void OnAddBlockClicked(object sender, MouseButtonEventArgs e) => ViewModel?.AddBlockCommand.Execute(null);

    private void OnAddAreaKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            ViewModel?.AddBlockCommand.Execute(null);
        }
        else if (e.Key == Key.Up)
        {
            e.Handled = true;
            AddArea.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
        }
    }

    // A block that has the keyboard focus without being edited: the arrow keys move on, Enter opens it.
    private void OnBlockKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource != sender || sender is not FrameworkElement { DataContext: BlockViewModel block } element)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter or Key.F2:
                ViewModel?.BeginEdit(block);
                break;
            case Key.Down:
                element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                break;
            case Key.Up:
                element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnEditorShown(object sender, RoutedEventArgs e) => FocusEditor(sender);

    private void OnEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => FocusEditor(sender);

    private void FocusEditor(object sender)
    {
        if (sender is not TextBox { IsVisible: true, DataContext: BlockViewModel { IsEditing: true } } editor)
        {
            return;
        }

        // Coming from the block below, the caret belongs at the end; coming from above, at the start.
        var caretAtStart = _nextCaretAtStart;
        _nextCaretAtStart = false;

        // The field has only just become visible; it can take the focus once the layout has caught up.
        editor.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (editor.IsVisible && !editor.IsKeyboardFocused)
            {
                editor.Focus();
                editor.CaretIndex = caretAtStart ? 0 : editor.Text.Length;
                editor.BringIntoView();
            }
        });
    }

    private void OnEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: BlockViewModel { IsEditing: true } })
        {
            ViewModel?.CommitEditing();
        }
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor || ViewModel is not { } viewModel)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var line = editor.GetLineIndexFromCharacterIndex(editor.CaretIndex);
        var hasSelection = editor.SelectionLength > 0;
        switch (e.Key)
        {
            case Key.Escape:
                // Handled here so that Escape ends the editing instead of closing the panel.
                var index = viewModel.Blocks.IndexOf((BlockViewModel)editor.DataContext);
                viewModel.CommitEditing();
                FocusBlock(index);
                break;
            case Key.Enter when modifiers == ModifierKeys.Control:
                viewModel.CommitAndInsertBelow();
                break;
            case Key.Tab when modifiers == ModifierKeys.None:
                _nextCaretAtStart = true;
                viewModel.EditAdjacent(1);
                break;
            case Key.Tab when modifiers == ModifierKeys.Shift:
                viewModel.EditAdjacent(-1);
                break;

            // The arrow keys leave the block only from its first and last line, like the edge of a page.
            case Key.Down when modifiers == ModifierKeys.None && !hasSelection && line == editor.LineCount - 1:
                _nextCaretAtStart = true;
                viewModel.EditAdjacent(1);
                break;
            case Key.Up when modifiers == ModifierKeys.None && !hasSelection && line <= 0:
                viewModel.EditAdjacent(-1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Gives the keyboard focus to a block that is shown rendered, so the keyboard can carry on from there.</summary>
    private void FocusBlock(int index) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        if (ViewModel is not { } viewModel || viewModel.Blocks.Count == 0)
        {
            AddArea.Focus();
            return;
        }

        var container = BlockList.ItemContainerGenerator.ContainerFromIndex(Math.Clamp(index, 0, viewModel.Blocks.Count - 1));
        if (container is not null && VisualTreeHelper.GetChildrenCount(container) > 0 && VisualTreeHelper.GetChild(container, 0) is UIElement { IsVisible: true } block)
        {
            block.Focus();
        }
        else
        {
            AddArea.Focus();
        }
    });
    private void OnTitleEditRequested(object? sender, EventArgs e) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        TitleBox.Focus();
        TitleBox.SelectAll();
    });

    private void OnTitleLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitTitle();

    private void OnTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitTitle();
            Keyboard.ClearFocus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ResetTitle();
            Keyboard.ClearFocus();
        }
    }

    private void CommitTitle()
    {
        if (ViewModel is { IsScratchpad: false } viewModel && TitleBox.Text != viewModel.Title)
        {
            viewModel.Rename(TitleBox.Text);
            ResetTitle();
        }
    }

    // Typing replaced the one-way binding's value locally; this shows the title the note really has.
    private void ResetTitle() => TitleBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
}
