using Meerkat.Abstractions;
using Meerkat.App.Settings;

namespace Meerkat.App.Overlay;

public sealed class OverlayPanelHost(OverlayViewModel viewModel, LayoutService layouts) : IOverlayPanelHost
{
    public IDisposable Show(OverlayPanel panel)
    {
        var defaultLayout = new WidgetLayout
        {
            Left = panel.DefaultBounds.Left,
            Top = panel.DefaultBounds.Top,
            Width = panel.DefaultBounds.Width,
            Height = panel.DefaultBounds.Height,
        };
        var host = new WidgetHostViewModel(panel.Id, panel.Title, defaultLayout)
        {
            Content = panel.View,
            HeaderContent = panel.HeaderView,
        };
        host.ApplyLayout(layouts.Find(panel.Id) ?? defaultLayout);
        viewModel.AddHost(host);
        return new PanelHandle(() => viewModel.RemoveHost(host));
    }

    private sealed class PanelHandle(Action close) : IDisposable
    {
        private Action? _close = close;

        public void Dispose()
        {
            _close?.Invoke();
            _close = null;
        }
    }
}
