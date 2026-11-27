using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;
using Meerkat.Widgets.Git.Providers;
using Meerkat.Widgets.Git.ViewModels;
using Meerkat.Widgets.Git.Views;

namespace Meerkat.Widgets.Git.Settings;

public sealed class GitSettingsPage(GitSettingsSource settingsSource, IGitProviderFactory providerFactory) : IWidgetSettingsPage
{
    private GitSettingsPageViewModel? _viewModel;

    public FrameworkElement CreateView()
    {
        _viewModel = new GitSettingsPageViewModel(settingsSource, providerFactory);
        return new GitSettingsView { DataContext = _viewModel };
    }

    public void Apply()
    {
        if (_viewModel is not null)
        {
            settingsSource.Update(_viewModel.ToSettings(), _viewModel.Token);
        }
    }
}

public sealed partial class ProjectOptionViewModel(GitProject project, bool isSelected) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = isSelected;

    public GitProject Project { get; } = project;
}

public sealed partial class GitSettingsPageViewModel : ObservableObject
{
    private readonly GitSettingsSource _settingsSource;
    private readonly IGitProviderFactory _providerFactory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGitLab))]
    [NotifyPropertyChangedFor(nameof(IsGitHub))]
    [NotifyPropertyChangedFor(nameof(TokenLabel))]
    [NotifyPropertyChangedFor(nameof(ProjectsLabel))]
    [NotifyPropertyChangedFor(nameof(OnlyMineLabel))]
    private GitProviderKind _provider;

    [ObservableProperty]
    private string _serverUrl;

    /// <summary>A newly entered token; empty keeps the stored one.</summary>
    [ObservableProperty]
    private string _token = string.Empty;

    [ObservableProperty]
    private bool _onlyMyBranches;

    [ObservableProperty]
    private double _intervalSeconds;

    [ObservableProperty]
    private string _connectionStatus = string.Empty;

    [ObservableProperty]
    private bool _isConnectionError;

    public GitSettingsPageViewModel(GitSettingsSource settingsSource, IGitProviderFactory providerFactory)
    {
        _settingsSource = settingsSource;
        _providerFactory = providerFactory;

        var current = settingsSource.Current;
        _provider = current.Provider;
        _serverUrl = current.ServerUrl;
        _onlyMyBranches = current.OnlyMyBranches;
        _intervalSeconds = current.IntervalSeconds;
        foreach (var project in current.Projects)
        {
            Projects.Add(new ProjectOptionViewModel(project, isSelected: true));
        }

        if (!string.IsNullOrEmpty(settingsSource.ReadToken(current)))
        {
            ConnectionStatus = "A token is stored. Enter a new one to replace it.";
        }
    }

    public ObservableCollection<ProjectOptionViewModel> Projects { get; } = [];

    public bool IsGitLab
    {
        get => Provider == GitProviderKind.GitLab;
        set => SelectProvider(value, GitProviderKind.GitLab);
    }

    public bool IsGitHub
    {
        get => Provider == GitProviderKind.GitHub;
        set => SelectProvider(value, GitProviderKind.GitHub);
    }

    public string TokenLabel => $"Personal access token ({GitTerminology.For(Provider).TokenScopeHint})";

    public string ProjectsLabel => Provider == GitProviderKind.GitHub ? "Repositories" : "Projects";

    public string OnlyMineLabel => $"Only {GitTerminology.For(Provider).PipelinesTitle.ToLowerInvariant()} I triggered";

    public GitWidgetSettings ToSettings() => new()
    {
        Provider = Provider,
        ServerUrl = ServerUrl.Trim(),
        Projects = [.. Projects.Where(option => option.IsSelected).Select(option => option.Project)],
        OnlyMyBranches = OnlyMyBranches,
        IntervalSeconds = IntervalSeconds,
    };

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsConnectionError = false;
        ConnectionStatus = "Connecting …";
        try
        {
            var settings = ToSettings();
            var token = string.IsNullOrWhiteSpace(Token) ? _settingsSource.ReadToken(settings) : Token;
            using var provider = _providerFactory.Create(settings, token)
                ?? throw new GitProviderException(GitFailureKind.Unauthorized, "Enter an access token first.");

            var userName = await provider.GetUserNameAsync(CancellationToken.None);
            var available = await provider.ListProjectsAsync(CancellationToken.None);

            // Keep what was selected, including projects the provider no longer lists first.
            var selected = Projects.Where(option => option.IsSelected).Select(option => option.Project).ToList();
            Projects.Clear();
            foreach (var project in selected.Concat(available.Where(project => selected.All(chosen => chosen.Id != project.Id))))
            {
                Projects.Add(new ProjectOptionViewModel(project, selected.Any(chosen => chosen.Id == project.Id)));
            }

            ConnectionStatus = $"Connected as {userName}. Select the {ProjectsLabel.ToLowerInvariant()} to watch.";
        }
        catch (Exception exception)
        {
            IsConnectionError = true;
            ConnectionStatus = exception.Message;
        }
    }

    private void SelectProvider(bool isSelected, GitProviderKind kind)
    {
        if (!isSelected || Provider == kind)
        {
            return;
        }

        // Follow the provider with the server address unless the user entered one of their own.
        if (ServerUrl is GitWidgetSettings.DefaultGitLabUrl or GitWidgetSettings.DefaultGitHubUrl or "")
        {
            ServerUrl = kind == GitProviderKind.GitHub ? GitWidgetSettings.DefaultGitHubUrl : GitWidgetSettings.DefaultGitLabUrl;
        }

        // Projects belong to one provider and mean nothing to the other.
        Projects.Clear();
        ConnectionStatus = string.Empty;
        Provider = kind;
    }
}
