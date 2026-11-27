using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;
using Meerkat.Widgets.Services.Checks;
using Meerkat.Widgets.Services.Views;

namespace Meerkat.Widgets.Services.Settings;

public sealed class ServicesSettingsPage(ServicesSettingsSource settingsSource, IServiceChecker checker) : IWidgetSettingsPage
{
    private ServicesSettingsPageViewModel? _viewModel;

    public FrameworkElement CreateView()
    {
        _viewModel = new ServicesSettingsPageViewModel(settingsSource.Current, checker);
        return new ServicesSettingsView { DataContext = _viewModel };
    }

    public void Apply()
    {
        if (_viewModel is not null)
        {
            settingsSource.Update(_viewModel.ToSettings());
        }
    }
}

public sealed record IntervalOption(double Seconds, string DisplayName);

/// <summary>A well-known service whose status page offers the API the "Status page" template reads.</summary>
public sealed record ServicePreset(string Name, string Url);

public sealed partial class ServicesSettingsPageViewModel : ObservableObject
{
    private static readonly ServicePreset[] AllPresets =
    [
        new("GitHub", "https://www.githubstatus.com"),
        new("npm", "https://status.npmjs.org"),
        new("Docker Hub", "https://www.dockerstatus.com"),
        new("Maven Central", "https://status.maven.org"),
    ];

    private readonly IServiceChecker _checker;
    private ServiceEndpoint? _editing;

    [ObservableProperty]
    private bool _isFormOpen;

    [ObservableProperty]
    private string _formTitle = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequestHint))]
    private string _url = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusPage), nameof(IsHttpStatus), nameof(IsCustomJson), nameof(RequestHint), nameof(UrlLabel))]
    private ServiceKind _kind;

    [ObservableProperty]
    private IntervalOption _selectedInterval;

    [ObservableProperty]
    private string _statePath = string.Empty;

    [ObservableProperty]
    private string _okValues = string.Empty;

    [ObservableProperty]
    private string _warningValues = string.Empty;

    [ObservableProperty]
    private string _descriptionPath = string.Empty;

    [ObservableProperty]
    private string? _formError;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private bool _hasTestResult;

    /// <summary>Status code and duration of the test request, e.g. "200 · 184 ms".</summary>
    [ObservableProperty]
    private string _testSummary = string.Empty;

    [ObservableProperty]
    private string _testResultText = string.Empty;

    [ObservableProperty]
    private WidgetSeverity _testSeverity;

    [ObservableProperty]
    private string _testExcerpt = string.Empty;

    public ServicesSettingsPageViewModel(ServicesWidgetSettings settings, IServiceChecker checker)
    {
        _checker = checker;
        foreach (var endpoint in settings.Endpoints)
        {
            Endpoints.Add(endpoint.Clone());
        }

        _selectedInterval = Intervals[1];
        RefreshPresets();
    }

    public ObservableCollection<ServiceEndpoint> Endpoints { get; } = [];

    /// <summary>The well-known services that were not added yet.</summary>
    public ObservableCollection<ServicePreset> Presets { get; } = [];

    public IReadOnlyList<IntervalOption> Intervals { get; } =
    [
        new(60, "every minute"),
        new(300, "every 5 min"),
        new(900, "every 15 min"),
        new(1800, "every 30 min"),
        new(3600, "every hour"),
    ];

    // Each template is a radio button, which reports being switched off as well; only switching on counts.
    public bool IsStatusPage
    {
        get => Kind == ServiceKind.StatusPage;
        set => SelectKind(ServiceKind.StatusPage, value);
    }

    public bool IsHttpStatus
    {
        get => Kind == ServiceKind.HttpStatus;
        set => SelectKind(ServiceKind.HttpStatus, value);
    }

    public bool IsCustomJson
    {
        get => Kind == ServiceKind.CustomJson;
        set => SelectKind(ServiceKind.CustomJson, value);
    }

    public string UrlLabel => Kind == ServiceKind.StatusPage ? "Base address of the status page" : "Address";

    /// <summary>Says in plain words what will be requested and how the answer is read.</summary>
    public string RequestHint
    {
        get
        {
            string address;
            try
            {
                address = BuildEndpoint().GetRequestUri().ToString();
            }
            catch (ArgumentException)
            {
                address = "the address";
            }

            return Kind switch
            {
                ServiceKind.StatusPage => $"Requests {address}. State, incident text and affected components are read automatically.",
                ServiceKind.HttpStatus => $"Requests {address}. A success status code (2xx) means the service is fine; anything else, or no answer, is an outage.",
                _ => $"Requests {address} and reads the state from the value at the path below.",
            };
        }
    }

    public ServicesWidgetSettings ToSettings() => new() { Endpoints = [.. Endpoints.Select(endpoint => endpoint.Clone())] };

    [RelayCommand]
    private void AddEndpoint()
    {
        _editing = null;
        LoadForm(new ServiceEndpoint(), "Add service");
    }

    [RelayCommand]
    private void EditEndpoint(ServiceEndpoint endpoint)
    {
        _editing = endpoint;
        LoadForm(endpoint, $"Edit {endpoint.Name}");
    }

    [RelayCommand]
    private void RemoveEndpoint(ServiceEndpoint endpoint)
    {
        Endpoints.Remove(endpoint);
        if (_editing == endpoint)
        {
            IsFormOpen = false;
        }

        RefreshPresets();
    }

    [RelayCommand]
    private void AddPreset(ServicePreset preset)
    {
        Endpoints.Add(new ServiceEndpoint { Name = preset.Name, Url = preset.Url, Kind = ServiceKind.StatusPage });
        RefreshPresets();
    }

    [RelayCommand]
    private void CancelForm() => IsFormOpen = false;

    /// <summary>Sends the request once and shows what Meerkat would make of the answer, without saving anything.</summary>
    [RelayCommand]
    private async Task TestAsync()
    {
        if (TryBuildValidEndpoint() is not { } endpoint)
        {
            return;
        }

        IsTesting = true;
        HasTestResult = false;
        try
        {
            var check = await _checker.CheckAsync(endpoint, CancellationToken.None);
            TestSummary = $"{(check.StatusCode is { } code ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "no answer")} · {check.Elapsed.TotalMilliseconds:0} ms";
            TestSeverity = check.State.Severity;
            TestResultText = $"Result: {DescribeSeverity(check.State.Severity)} · “{check.State.StatusText}”";
            TestExcerpt = check.Excerpt;
            HasTestResult = true;
        }
        catch (Exception exception) when (exception is ServiceCheckException or ArgumentException)
        {
            FormError = exception.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (TryBuildValidEndpoint() is not { } endpoint)
        {
            return;
        }

        if (_editing is not null && Endpoints.IndexOf(_editing) is >= 0 and var index)
        {
            Endpoints[index] = endpoint;
        }
        else
        {
            Endpoints.Add(endpoint);
        }

        IsFormOpen = false;
        RefreshPresets();
    }

    private static string DescribeSeverity(WidgetSeverity severity) => severity switch
    {
        WidgetSeverity.Ok => "green",
        WidgetSeverity.Warning => "yellow",
        _ => "red",
    };

    private void SelectKind(ServiceKind kind, bool isSelected)
    {
        if (isSelected)
        {
            Kind = kind;
            HasTestResult = false;
        }
    }

    private void LoadForm(ServiceEndpoint endpoint, string title)
    {
        FormTitle = title;
        Name = endpoint.Name;
        Url = endpoint.Url;
        Kind = endpoint.Kind;
        SelectedInterval = Intervals.FirstOrDefault(option => option.Seconds >= endpoint.IntervalSeconds) ?? Intervals[^1];
        StatePath = endpoint.StatePath;
        OkValues = string.Join(", ", endpoint.OkValues);
        WarningValues = string.Join(", ", endpoint.WarningValues);
        DescriptionPath = endpoint.DescriptionPath;
        FormError = null;
        HasTestResult = false;
        IsFormOpen = true;

        // Radio buttons that are created for the form read their state only when told it changed.
        OnPropertyChanged(nameof(IsStatusPage));
        OnPropertyChanged(nameof(IsHttpStatus));
        OnPropertyChanged(nameof(IsCustomJson));
    }

    private ServiceEndpoint BuildEndpoint() => new()
    {
        Id = _editing?.Id ?? Guid.NewGuid().ToString("N"),
        Name = Name.Trim(),
        Url = Url.Trim(),
        Kind = Kind,
        IntervalSeconds = SelectedInterval.Seconds,
        StatePath = StatePath.Trim(),
        OkValues = SplitValues(OkValues),
        WarningValues = SplitValues(WarningValues),
        DescriptionPath = DescriptionPath.Trim(),
        IsMuted = _editing?.IsMuted ?? false,
    };

    private ServiceEndpoint? TryBuildValidEndpoint()
    {
        try
        {
            var endpoint = BuildEndpoint();
            endpoint.Validate();
            FormError = null;
            return endpoint;
        }
        catch (ArgumentException exception)
        {
            FormError = exception.Message;
            return null;
        }
    }

    private void RefreshPresets()
    {
        Presets.Clear();
        foreach (var preset in AllPresets.Where(preset => !Endpoints.Any(endpoint => endpoint.Url.Contains(new Uri(preset.Url).Host, StringComparison.OrdinalIgnoreCase))))
        {
            Presets.Add(preset);
        }
    }

    private static List<string> SplitValues(string text) =>
        [.. text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
