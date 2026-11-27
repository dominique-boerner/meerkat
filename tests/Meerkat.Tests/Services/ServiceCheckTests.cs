using System.Net;
using System.Net.Http;
using Meerkat.Abstractions;
using Meerkat.Tests.Git;
using Meerkat.Widgets.Services.Checks;
using Meerkat.Widgets.Services.Settings;

namespace Meerkat.Tests.Services;

public class ServiceCheckTests
{
    private const string Disturbed = """
        {
          "page": { "name": "Docker", "url": "https://www.dockerstatus.com" },
          "status": { "indicator": "minor", "description": "Partial System Outage" },
          "components": [
            { "name": "Docker Hub", "status": "partial_outage", "group": true },
            { "name": "Registry", "status": "partial_outage", "group": false },
            { "name": "Web", "status": "operational", "group": false },
            { "name": "Automated Builds", "status": "operational" }
          ],
          "incidents": [
            {
              "name": "Increased error rate when pulling images",
              "started_at": "2026-11-27T15:24:00.000Z",
              "incident_updates": [
                { "body": "Cause found,\n<b>fix</b> is being rolled out.", "created_at": "2026-11-27T15:36:00.000Z" },
                { "body": "We are investigating.", "created_at": "2026-11-27T15:25:00.000Z" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void StatusPage_ReportsIncidentUpdateAndComponents()
    {
        var (state, excerpt) = ServiceResponseParser.ParseStatusPage(Disturbed);

        Assert.Equal(WidgetSeverity.Warning, state.Severity);
        Assert.Equal("Partial System Outage", state.StatusText);
        Assert.Equal("Increased error rate when pulling images", state.Incident);
        Assert.Equal("Cause found, fix is being rolled out.", state.LatestUpdate);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 15, 36, 0, TimeSpan.Zero), state.LatestUpdateAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 15, 24, 0, TimeSpan.Zero), state.Since);

        // Groups only repeat what their members say.
        Assert.Equal(
            [new ComponentState("Registry", WidgetSeverity.Warning), new ComponentState("Web", WidgetSeverity.Ok), new ComponentState("Automated Builds", WidgetSeverity.Ok)],
            state.Components);
        Assert.Contains("\"indicator\": \"minor\"", excerpt);
        Assert.DoesNotContain("components", excerpt);
    }

    [Theory]
    [InlineData("none", WidgetSeverity.Ok)]
    [InlineData("minor", WidgetSeverity.Warning)]
    [InlineData("maintenance", WidgetSeverity.Warning)]
    [InlineData("major", WidgetSeverity.Error)]
    [InlineData("critical", WidgetSeverity.Error)]
    public void StatusPage_MapsTheIndicatorToASeverity(string indicator, WidgetSeverity expected)
    {
        var json = $$"""{ "status": { "indicator": "{{indicator}}", "description": "Text" } }""";

        var (state, _) = ServiceResponseParser.ParseStatusPage(json);

        Assert.Equal(expected, state.Severity);
        Assert.Null(state.Incident);
        Assert.Empty(state.Components!);
    }

    [Theory]
    [InlineData("<html>not json</html>")]
    [InlineData("""{ "something": "else" }""")]
    public void StatusPage_RejectsAnswersThatAreNoSummary(string body)
    {
        Assert.Throws<ServiceCheckException>(() => ServiceResponseParser.ParseStatusPage(body));
    }

    [Theory]
    [InlineData("ok", WidgetSeverity.Ok, "ok")]
    [InlineData("UP", WidgetSeverity.Ok, "UP")]
    [InlineData("degraded", WidgetSeverity.Warning, "degraded")]
    [InlineData("down", WidgetSeverity.Error, "down")]
    public void CustomJson_ComparesTheValueAtThePath(string value, WidgetSeverity expected, string expectedText)
    {
        var endpoint = new ServiceEndpoint { Kind = ServiceKind.CustomJson, StatePath = "checks[1].state", OkValues = ["ok", "up"], WarningValues = ["degraded"] };
        var json = $$"""{ "checks": [ { "state": "ignored" }, { "state": "{{value}}" } ] }""";

        var (state, _) = ServiceResponseParser.ParseCustomJson(json, endpoint);

        Assert.Equal(expected, state.Severity);
        Assert.Equal(expectedText, state.StatusText);
    }

    [Fact]
    public void CustomJson_UsesTheDescriptionAndAcceptsNumbersAndBooleans()
    {
        var endpoint = new ServiceEndpoint { Kind = ServiceKind.CustomJson, StatePath = "healthy", OkValues = ["true"], DescriptionPath = "message" };

        var (state, excerpt) = ServiceResponseParser.ParseCustomJson("""{ "healthy": true, "message": "All good", "code": 200 }""", endpoint);

        Assert.Equal(WidgetSeverity.Ok, state.Severity);
        Assert.Equal("All good", state.StatusText);
        Assert.Contains("\"healthy\": true", excerpt);
    }

    [Fact]
    public void CustomJson_ReportsAMissingPath()
    {
        var endpoint = new ServiceEndpoint { Kind = ServiceKind.CustomJson, StatePath = "status.indicator", OkValues = ["none"] };

        var exception = Assert.Throws<ServiceCheckException>(() => ServiceResponseParser.ParseCustomJson("""{ "status": {} }""", endpoint));

        Assert.Contains("status.indicator", exception.Message);
    }

    [Theory]
    [InlineData("https://www.githubstatus.com", "https://www.githubstatus.com/api/v2/summary.json", "https://www.githubstatus.com/")]
    [InlineData("https://status.example.com/", "https://status.example.com/api/v2/summary.json", "https://status.example.com/")]
    [InlineData("https://status.example.com/api/v2/status.json", "https://status.example.com/api/v2/status.json", "https://status.example.com/")]
    public void StatusPage_RequestsTheSummaryBelowItsBaseAddress(string url, string expectedRequest, string expectedPage)
    {
        var endpoint = new ServiceEndpoint { Name = "Service", Kind = ServiceKind.StatusPage, Url = url };

        Assert.Equal(expectedRequest, endpoint.GetRequestUri().ToString());
        Assert.Equal(expectedPage, endpoint.GetPageUri().ToString());
    }

    [Theory]
    [InlineData("status.example.com")]
    [InlineData("ftp://status.example.com")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("")]
    public void Endpoint_OnlyAcceptsWebAddresses(string url)
    {
        var endpoint = new ServiceEndpoint { Name = "Service", Kind = ServiceKind.HttpStatus, Url = url };

        Assert.Throws<ArgumentException>(endpoint.Validate);
    }

    [Fact]
    public async Task Checker_ReadsAStatusPageWithOneRequest()
    {
        var handler = new FakeHttpHandler().Respond("/api/v2/summary.json", Disturbed);
        var checker = new HttpServiceChecker(new HttpClient(handler));

        var check = await checker.CheckAsync(new ServiceEndpoint { Name = "Docker Hub", Url = "https://www.dockerstatus.com" }, CancellationToken.None);

        Assert.Equal(WidgetSeverity.Warning, check.State.Severity);
        Assert.Equal(200, check.StatusCode);
        Assert.Equal(["GET /api/v2/summary.json"], handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, WidgetSeverity.Ok, "Reachable")]
    [InlineData(HttpStatusCode.NoContent, WidgetSeverity.Ok, "Reachable")]
    [InlineData(HttpStatusCode.ServiceUnavailable, WidgetSeverity.Error, "Answers with HTTP 503")]
    public async Task Checker_JudgesAPlainAddressByItsStatusCode(HttpStatusCode status, WidgetSeverity expected, string expectedText)
    {
        var checker = new HttpServiceChecker(new HttpClient(new FakeHttpHandler().Respond("/health", "ignored", status)));

        var check = await checker.CheckAsync(new ServiceEndpoint { Name = "API", Kind = ServiceKind.HttpStatus, Url = "https://api.example.com/health" }, CancellationToken.None);

        Assert.Equal(expected, check.State.Severity);
        Assert.Equal(expectedText, check.State.StatusText);
        Assert.Equal((int)status, check.StatusCode);
    }

    [Fact]
    public async Task Checker_TreatsAnUnreachableAddressAsOutageButAnUnreachableStatusPageAsUnknown()
    {
        var checker = new HttpServiceChecker(new HttpClient(new FailingHandler()));

        var plain = await checker.CheckAsync(new ServiceEndpoint { Name = "API", Kind = ServiceKind.HttpStatus, Url = "https://api.example.com" }, CancellationToken.None);
        Assert.Equal(WidgetSeverity.Error, plain.State.Severity);
        Assert.Equal("Not reachable", plain.State.StatusText);
        Assert.Null(plain.StatusCode);

        // A status page that cannot be read says nothing about the service it reports on.
        await Assert.ThrowsAsync<ServiceCheckException>(() =>
            checker.CheckAsync(new ServiceEndpoint { Name = "GitHub", Kind = ServiceKind.StatusPage, Url = "https://www.githubstatus.com" }, CancellationToken.None));
    }

    [Fact]
    public async Task Checker_ReportsAStatusPageThatAnswersWithAnError()
    {
        var checker = new HttpServiceChecker(new HttpClient(new FakeHttpHandler().Respond("/api/v2/summary.json", "nope", HttpStatusCode.NotFound)));

        var exception = await Assert.ThrowsAsync<ServiceCheckException>(() =>
            checker.CheckAsync(new ServiceEndpoint { Name = "GitLab", Url = "https://status.gitlab.com" }, CancellationToken.None));

        Assert.Contains("404", exception.Message);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No such host is known.");
    }
}
