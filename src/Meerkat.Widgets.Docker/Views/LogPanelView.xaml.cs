using System.Windows.Controls;
using Meerkat.Widgets.Docker.ViewModels;

namespace Meerkat.Widgets.Docker.Views;

public partial class LogPanelView : UserControl
{
    public LogPanelView()
    {
        InitializeComponent();
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer scrollViewer || DataContext is not LogPanelViewModel viewModel)
        {
            return;
        }

        if (e.ExtentHeightChange == 0)
        {
            // The user scrolled (or the panel was resized): follow only while the end is in view.
            viewModel.IsFollowing = scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 1;
        }
        else if (viewModel.IsFollowing)
        {
            scrollViewer.ScrollToEnd();
        }
    }
}
