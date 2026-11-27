using Meerkat.Widgets.Server.Monitoring;
using Meerkat.Widgets.Server.Settings;
using Meerkat.Widgets.Server.Ssh;

namespace Meerkat.Tests.Server;

internal sealed class FakeSshClient : ISshClient
{
    public List<string> Calls { get; } = [];

    public SshResult Result { get; set; } = new(0, string.Empty, string.Empty, TimeSpan.FromMilliseconds(23));

    public Exception? Failure { get; set; }

    public List<SshTarget> Targets { get; } = [];

    public Task<SshResult> RunAsync(SshTarget target, string command, HostKeyPolicy policy, CancellationToken cancellationToken)
    {
        Targets.Add(target);
        Calls.Add($"run {target.Address} {policy}");
        return Failure is null ? Task.FromResult(Result) : Task.FromException<SshResult>(Failure);
    }

    public Task ForgetHostKeyAsync(SshTarget target, CancellationToken cancellationToken)
    {
        Calls.Add($"forget {target.Address}");
        return Task.CompletedTask;
    }

    public void OpenTerminal(SshTarget target) => Calls.Add($"terminal {target.Address}");
}

internal sealed class FakeServerProbe : IServerProbe
{
    public Dictionary<string, Queue<object>> Responses { get; } = [];

    public List<string> Calls { get; } = [];

    /// <summary>Queues what the next check of a host returns: a snapshot, or an exception to throw.</summary>
    public void Enqueue(string address, object response)
    {
        if (!Responses.TryGetValue(address, out var queue))
        {
            Responses[address] = queue = new Queue<object>();
        }

        queue.Enqueue(response);
    }

    public List<string?> Passwords { get; } = [];

    public Task<HostSnapshot> ProbeAsync(ServerHost host, HostKeyPolicy policy, CancellationToken cancellationToken, string? password = null)
    {
        Passwords.Add(password);
        Calls.Add($"probe {host.Address} {policy}");
        return Next(host);
    }

    public Task<HostSnapshot> TrustHostKeyAsync(ServerHost host, CancellationToken cancellationToken)
    {
        Calls.Add($"trust {host.Address}");
        return Next(host);
    }

    public void OpenTerminal(ServerHost host) => Calls.Add($"terminal {host.Address}");

    public static HostSnapshot Snapshot(
        double diskUsedPercent = 42,
        long cpuTotal = 1000,
        long cpuIdle = 900,
        params ServiceState[] services) => new(
        "Ubuntu 24.04.5 LTS",
        TimeSpan.FromDays(41.5),
        2.14,
        1.87,
        1.42,
        8,
        new CpuTimes(cpuTotal, cpuIdle),
        32L << 30,
        (32L << 30) - (long)(18.6 * (1L << 30)),
        500_000_000_000,
        (long)(500_000_000_000 * diskUsedPercent / 100),
        services,
        TimeSpan.FromMilliseconds(23));

    private Task<HostSnapshot> Next(ServerHost host)
    {
        var response = Responses.TryGetValue(host.Address, out var queue) && queue.Count > 0 ? queue.Dequeue() : Snapshot();
        return response is Exception exception ? Task.FromException<HostSnapshot>(exception) : Task.FromResult((HostSnapshot)response);
    }
}
