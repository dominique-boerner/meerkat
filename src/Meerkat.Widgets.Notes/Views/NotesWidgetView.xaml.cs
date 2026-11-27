using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private void OnEditorShown(object sender, RoutedEventArgs e) => FocusEditor(sender);

    private void OnEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => FocusEditor(sender);

    private static void FocusEditor(object sender)
    {
        if (sender is not TextBox { IsVisible: true, DataContext: BlockViewModel { IsEditing: true } } editor)
        {
            return;
        }

        // The field has only just become visible; it can take the focus once the layout has caught up.
        editor.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (editor.IsVisible && !editor.IsKeyboardFocused)
            {
                editor.Focus();
                editor.CaretIndex = editor.Text.Length;
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
        var isCommit = e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        if (isCommit)
        {
            // Handled here so that Escape ends the editing instead of closing the panel.
            e.Handled = true;
            ViewModel?.CommitEditing();
        }
    }

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
