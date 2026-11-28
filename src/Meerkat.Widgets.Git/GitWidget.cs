using System.Windows;
using Meerkat.Abstractions;
using Meerkat.Widgets.Git.Providers;
using Meerkat.Widgets.Git.Settings;
using Meerkat.Widgets.Git.ViewModels;
using Meerkat.Widgets.Git.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Meerkat.Widgets.Git;

/// <summary>Shows pipelines and change requests from GitLab or GitHub.</summary>
public sealed class GitWidget(GitWidgetViewModel viewModel, GitSettingsPage settingsPage) : IOverlayWidget
{
    public const string WidgetId = "git";

    private GitWidgetView? _view;
    private GitHeaderView? _headerView;

    public event EventHandler? StatusChanged
    {
        add => viewModel.StatusChanged += value;
        remove => viewModel.StatusChanged -= value;
    }

    public string Id => WidgetId;

    public string Title => "Git";

    public string Icon => "";

    public FrameworkElement View => _view ??= new GitWidgetView { DataContext = viewModel };

    public FrameworkElement HeaderView => _headerView ??= new GitHeaderView { DataContext = viewModel };

    public IWidgetSettingsPage SettingsPage => settingsPage;

    public WidgetStatus? Status => viewModel.Status;

    public IReadOnlyList<WidgetAlert> Alerts => viewModel.Alerts;

    public IReadOnlyList<WidgetAction> Actions => viewModel.Actions;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        viewModel.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => viewModel.StopAsync();
}

public static class GitWidgetServiceCollectionExtensions
{
    public static IServiceCollection AddGitWidget(this IServiceCollection services)
    {
        services.AddSingleton<IGitProviderFactory, GitProviderFactory>();
        services.AddSingleton<GitSettingsSource>();
        services.AddSingleton(provider => new GitWidgetViewModel(
            provider.GetRequiredService<IGitProviderFactory>(),
            provider.GetRequiredService<GitSettingsSource>(),
            provider.GetRequiredService<IOverlayState>(),
            provider.GetRequiredService<IOverlayCommands>(),
            TimeProvider.System));
        services.AddSingleton<GitSettingsPage>();
        services.AddSingleton<IOverlayWidget, GitWidget>();
        return services;
    }
}
