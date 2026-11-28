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
    public FrameworkElement CreateView()
    {
        // The token is not among what is stored here: it is stored once a connection proved it works.
        var viewModel = new GitSettingsPageViewModel(settingsSource, providerFactory);
        viewModel.Changed += (_, _) => settingsSource.Update(viewModel.ToSettings(), newToken: null);
        return new GitSettingsView { DataContext = viewModel };
    }
}

public sealed partial class ProjectOptionViewModel(GitProject project, bool isSelected) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = isSelected;

    /// <summary>False while the project is hidden by the search text.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    public GitProject Project { get; } = project;
}

public enum GitConnectionState
{
    /// <summary>No token is stored and none was entered.</summary>
    NotConnected,
    Connecting,
    Connected,
    Failed,
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
    [NotifyPropertyChangedFor(nameof(ShowStoredTokenPlaceholder))]
    private string _token = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStoredTokenPlaceholder))]
    private bool _hasStoredToken;

    [ObservableProperty]
    private bool _onlyMyBranches;

    [ObservableProperty]
    private double _intervalSeconds;

    [ObservableProperty]
    private GitConnectionState _connectionState;

    [ObservableProperty]
    private string _connectionStatus = string.Empty;

    [ObservableProperty]
    private string _projectFilter = string.Empty;

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
            AddProject(project, isSelected: true);
        }

        _hasStoredToken = !string.IsNullOrEmpty(settingsSource.ReadToken(current));

        // With a stored token the page connects right away, so the connection and all projects are visible
        // without the user having to ask for them.
        Initialization = _hasStoredToken ? ConnectAsync() : Task.CompletedTask;
    }

    /// <summary>Raised when something that is stored was changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Completes when the automatic connection attempt made while opening the page has finished.</summary>
    public Task Initialization { get; }

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

    public string ProjectsSummary => $"{ProjectsLabel} · {Projects.Count(option => option.IsSelected)} of {Projects.Count} selected";

    public string OnlyMineLabel => $"Only {GitTerminology.For(Provider).PipelinesTitle.ToLowerInvariant()} I triggered";

    /// <summary>The token itself is never read back into the page; dots stand in for it.</summary>
    public bool ShowStoredTokenPlaceholder => HasStoredToken && Token.Length == 0;

    public GitWidgetSettings ToSettings() => new()
    {
        Provider = Provider,
        ServerUrl = ServerUrl.Trim(),
        Projects = [.. Projects.Where(option => option.IsSelected).Select(option => option.Project)],
        OnlyMyBranches = OnlyMyBranches,
        IntervalSeconds = IntervalSeconds,
    };

    [RelayCommand]
    private Task TestConnectionAsync() => ConnectAsync();

    [RelayCommand]
    private void RemoveToken()
    {
        _settingsSource.RemoveToken(ToSettings());
        HasStoredToken = false;
        Token = string.Empty;
        ConnectionState = GitConnectionState.NotConnected;
        ConnectionStatus = "The token was removed.";

        // The widget still works with the token it was started with; announcing the settings makes it let go.
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnOnlyMyBranchesChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnIntervalSecondsChanged(double value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnServerUrlChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnProjectFilterChanged(string value)
    {
        foreach (var option in Projects)
        {
            option.IsVisible = value.Length == 0 || option.Project.Name.Contains(value.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task ConnectAsync()
    {
        ConnectionState = GitConnectionState.Connecting;
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
                AddProject(project, selected.Any(chosen => chosen.Id == project.Id));
            }

            OnProjectFilterChanged(ProjectFilter);
            ConnectionState = GitConnectionState.Connected;
            ConnectionStatus = $"Connected as {userName}";

            // A token that just proved itself is kept; one that failed never reaches the secret store.
            if (!string.IsNullOrWhiteSpace(Token))
            {
                _settingsSource.Update(settings, Token);
                HasStoredToken = true;
                Token = string.Empty;
            }
        }
        catch (Exception exception)
        {
            ConnectionState = GitConnectionState.Failed;
            ConnectionStatus = exception.Message;
        }

        OnPropertyChanged(nameof(ProjectsSummary));
    }

    private void AddProject(GitProject project, bool isSelected)
    {
        var option = new ProjectOptionViewModel(project, isSelected);
        option.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectOptionViewModel.IsSelected))
            {
                OnPropertyChanged(nameof(ProjectsSummary));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        };
        Projects.Add(option);
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
        Provider = kind;

        // Tokens are stored per provider and server, so the other provider may or may not have one.
        HasStoredToken = !string.IsNullOrEmpty(_settingsSource.ReadToken(ToSettings()));
        ConnectionState = GitConnectionState.NotConnected;
        ConnectionStatus = string.Empty;
        OnPropertyChanged(nameof(ProjectsSummary));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
