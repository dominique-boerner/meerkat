using System.ComponentModel;
using Meerkat.Tests.Git;
using Meerkat.Widgets.Server.Monitoring;
using Meerkat.Widgets.Server.Settings;
using Meerkat.Widgets.Server.Ssh;

namespace Meerkat.Tests.Server;

public class ServerProbeTests
{
    // Captured from a real Ubuntu 24.04 system; only the services section was extended.
    private const string SampleOutput = """
        ##os
        Ubuntu 24.04.5 LTS
        ##uptime
        10459.29 164729.27
        ##load
        0.53 0.39 0.37 1/407 42643
        ##cores
        16
        ##stat
        cpu  253229 0 25587 16472932 1562 0 18357 0 0 0
        ##mem
        MemTotal:       15918972 kB
        MemAvailable:   14967608 kB
        ##disk
        /dev/sdd        1055762868 2075928 999983468       1% /
        ##services
        docker active
        ssh inactive
        nonexistent-unit inactive
        ##end
        """;

    private static readonly ServerHost Host = new() { Name = "build-01", Address = "build-01.example.com", Services = ["docker"] };

    [Fact]
    public void ParseSnapshot_ReadsEverySectionOfARealAnswer()
    {
        var snapshot = ServerProbe.ParseSnapshot(SampleOutput, TimeSpan.FromMilliseconds(23));

        Assert.Equal("Ubuntu 24.04.5 LTS", snapshot.OsName);
        Assert.Equal(TimeSpan.FromSeconds(10459.29), snapshot.Uptime);
        Assert.Equal((0.53, 0.39, 0.37), (snapshot.Load1, snapshot.Load5, snapshot.Load15));
        Assert.Equal(16, snapshot.Cores);
        Assert.Equal(new CpuTimes(253229 + 25587 + 16472932 + 1562 + 18357, 16472932 + 1562), snapshot.Cpu);
        Assert.Equal(15918972L * 1024, snapshot.MemoryTotalBytes);
        Assert.Equal(14967608L * 1024, snapshot.MemoryAvailableBytes);
        Assert.Equal(1055762868L * 1024, snapshot.DiskTotalBytes);
        Assert.Equal(2075928L * 1024, snapshot.DiskUsedBytes);
        Assert.Equal(0.2, snapshot.DiskUsedPercent, precision: 1);
        Assert.Equal(6.0, snapshot.MemoryUsedPercent, precision: 1);
        Assert.Equal(
            [new ServiceState("docker", "active"), new ServiceState("ssh", "inactive"), new ServiceState("nonexistent-unit", "inactive")],
            snapshot.Services);
        Assert.True(snapshot.Services[0].IsActive);
        Assert.Equal(TimeSpan.FromMilliseconds(23), snapshot.ResponseTime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Welcome to RouterOS\n")]
    [InlineData("##os\nLinux\n##uptime\nnot-a-number\n##end")]
    public void ParseSnapshot_RejectsAnswersThatAreNotFromALinuxHost(string output)
    {
        var exception = Assert.Throws<ProbeException>(() => ServerProbe.ParseSnapshot(output, TimeSpan.Zero));

        Assert.Equal(ProbeFailureKind.Unsupported, exception.Kind);
    }

    [Fact]
    public void CpuTimes_YieldUsageBetweenTwoSamples()
    {
        var earlier = new CpuTimes(Total: 1000, Idle: 900);

        Assert.Equal(34, new CpuTimes(2000, 1560).UsageSince(earlier));
        Assert.Equal(0, new CpuTimes(2000, 1900).UsageSince(earlier));
        Assert.Null(earlier.UsageSince(earlier));
    }

    [Fact]
    public void BuildScript_ChecksTheRequestedServicesWithoutDoubleQuotes()
    {
        var script = ServerProbe.BuildScript(["docker", " gitlab-runner ", "", "getty@tty1.service"]);

        Assert.Contains("for s in docker gitlab-runner getty@tty1.service;", script);
        Assert.DoesNotContain('"', script);
        Assert.EndsWith("echo '##end'", script);
        Assert.DoesNotContain("for s in", ServerProbe.BuildScript([]));
    }

    [Theory]
    [InlineData("docker; rm -rf /")]
    [InlineData("$(reboot)")]
    [InlineData("-h")]
    [InlineData("a b")]
    public void BuildScript_RefusesServiceNamesThatWouldBecomeShellCode(string service)
    {
        Assert.Throws<ProbeException>(() => ServerProbe.BuildScript([service]));
    }

    [Theory]
    [InlineData("ssh: connect to host 10.0.0.9 port 22: Connection timed out", ProbeFailureKind.Unreachable, "SSH timeout")]
    [InlineData("ssh: Could not resolve hostname meerkat-test.invalid: Der angegebene Host ist unbekannt.", ProbeFailureKind.Unreachable, "Host name not found")]
    [InlineData("banner exchange: Connection to UNKNOWN port -1: Connection refused", ProbeFailureKind.Unreachable, "Connection refused")]
    [InlineData("Warning: Permanently added 'x' to the list of known hosts.\nssh: connect to host x port 22: Network is unreachable", ProbeFailureKind.Unreachable, "connect to host x port 22: Network is unreachable")]
    [InlineData("deploy@build-01: Permission denied (publickey).", ProbeFailureKind.AuthenticationFailed, "Authentication failed")]
    [InlineData("No ED25519 host key is known for build-01 and you have requested strict checking.\nHost key verification failed.", ProbeFailureKind.HostKeyUnknown, "The host key is not trusted yet.")]
    [InlineData("", ProbeFailureKind.Unreachable, "SSH connection failed")]
    public void ClassifyFailure_ExplainsWhySshGaveUp(string stderr, ProbeFailureKind expectedKind, string expectedMessage)
    {
        var exception = ServerProbe.ClassifyFailure(stderr);

        Assert.Equal(expectedKind, exception.Kind);
        Assert.Equal(expectedMessage, exception.Message);
    }

    [Fact]
    public void ClassifyFailure_ExtractsTheFingerprintOfAChangedHostKey()
    {
        const string stderr = """
            @@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@
            @    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @
            @@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@
            IT IS POSSIBLE THAT SOMEONE IS DOING SOMETHING NASTY!
            The fingerprint for the ED25519 key sent by the remote host is
            SHA256:Jm9uT2hlbGxvV29ybGRGaW5nZXJwcmludEV4YW1wbGU.
            Host key verification failed.
            """;

        var exception = ServerProbe.ClassifyFailure(stderr);

        Assert.Equal(ProbeFailureKind.HostKeyChanged, exception.Kind);
        Assert.Equal("SHA256:Jm9uT2hlbGxvV29ybGRGaW5nZXJwcmludEV4YW1wbGU", exception.Fingerprint);
    }

    [Fact]
    public async Task Probe_RunsOneStrictCallAndParsesItsOutput()
    {
        var ssh = new FakeSshClient { Result = new SshResult(0, SampleOutput, string.Empty, TimeSpan.FromMilliseconds(40)) };

        var snapshot = await new ServerProbe(ssh, new InMemorySecretStore()).ProbeAsync(Host, HostKeyPolicy.Strict, CancellationToken.None);

        Assert.Equal(["run build-01.example.com Strict"], ssh.Calls);
        Assert.Equal(16, snapshot.Cores);
        Assert.Equal(TimeSpan.FromMilliseconds(40), snapshot.ResponseTime);
    }

    [Fact]
    public async Task Probe_TranslatesSshFailuresAndAMissingClient()
    {
        var refused = new FakeSshClient { Result = new SshResult(255, string.Empty, "deploy@build-01: Permission denied (publickey).", TimeSpan.Zero) };
        var missing = new FakeSshClient { Failure = new Win32Exception(2) };

        var denied = await Assert.ThrowsAsync<ProbeException>(() => new ServerProbe(refused, new InMemorySecretStore()).ProbeAsync(Host, HostKeyPolicy.Strict, CancellationToken.None));
        var noClient = await Assert.ThrowsAsync<ProbeException>(() => new ServerProbe(missing, new InMemorySecretStore()).ProbeAsync(Host, HostKeyPolicy.Strict, CancellationToken.None));

        Assert.Equal(ProbeFailureKind.AuthenticationFailed, denied.Kind);
        Assert.Equal(ProbeFailureKind.Unreachable, noClient.Kind);
        Assert.Contains("ssh.exe", noClient.Message);
    }

    [Fact]
    public async Task TrustHostKey_ForgetsTheOldKeyBeforeAcceptingTheNewOne()
    {
        var ssh = new FakeSshClient { Result = new SshResult(0, SampleOutput, string.Empty, TimeSpan.Zero) };

        await new ServerProbe(ssh, new InMemorySecretStore()).TrustHostKeyAsync(Host, CancellationToken.None);

        Assert.Equal(["forget build-01.example.com", "run build-01.example.com AcceptNew"], ssh.Calls);
    }

    [Fact]
    public void SshArguments_NeverPromptAndCheckHostKeysStrictlyByDefault()
    {
        var target = new SshTarget("build-01.example.com", "deploy", 2222, SshAuthentication.KeyFile, @"C:\keys\id_ed25519");

        var strict = OpenSshClient.BuildArguments(target, HostKeyPolicy.Strict, "uptime");
        var acceptNew = OpenSshClient.BuildArguments(target, HostKeyPolicy.AcceptNew, "uptime");
        var terminal = OpenSshClient.BuildArguments(new SshTarget("nas", null, null, SshAuthentication.Agent, null), HostKeyPolicy.Strict, command: null);

        Assert.Equal(
            [
                "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", "-o", "ServerAliveInterval=5", "-o", "ServerAliveCountMax=2",
                "-o", "StrictHostKeyChecking=yes", "-p", "2222", "-l", "deploy", "-i", @"C:\keys\id_ed25519", "-o", "IdentitiesOnly=yes",
                "build-01.example.com", "uptime",
            ],
            strict);
        Assert.Contains("StrictHostKeyChecking=accept-new", acceptNew);

        // An interactive session leaves everything to the user's SSH configuration.
        Assert.Equal(["nas"], terminal);
    }

    [Fact]
    public void SshArguments_ForPasswordSignInAllowOneAttemptThroughTheHelper()
    {
        var target = new SshTarget("nas", "admin", null, SshAuthentication.Password, null, "s3cret");

        var arguments = OpenSshClient.BuildArguments(target, HostKeyPolicy.Strict, "uptime");
        var environment = OpenSshClient.BuildEnvironment(target, @"C:\Apps\Meerkat.exe");

        // BatchMode would forbid passwords altogether; a single attempt avoids locking the account.
        Assert.DoesNotContain("BatchMode=yes", arguments);
        Assert.Contains("NumberOfPasswordPrompts=1", arguments);
        Assert.Contains("PubkeyAuthentication=no", arguments);
        Assert.Contains("StrictHostKeyChecking=yes", arguments);

        // The password never appears on the command line, where other programs could read it.
        Assert.DoesNotContain(arguments, argument => argument.Contains("s3cret"));
        Assert.Equal(@"C:\Apps\Meerkat.exe", environment["SSH_ASKPASS"]);
        Assert.Equal("force", environment["SSH_ASKPASS_REQUIRE"]);
        Assert.Equal("s3cret", environment[OpenSshClient.PasswordEnvironmentVariable]);

        Assert.Empty(OpenSshClient.BuildEnvironment(new SshTarget("nas", null, null, SshAuthentication.Agent, null), null));
        Assert.Throws<ArgumentException>(() => OpenSshClient.BuildEnvironment(target with { Password = null }, @"C:\Apps\Meerkat.exe"));
        Assert.Throws<ArgumentException>(() => (target with { User = null }).Validate());
    }

    [Fact]
    public async Task Probe_SignsInWithTheStoredPasswordUnlessOneIsHandedOver()
    {
        var host = new ServerHost { Name = "nas", Address = "nas", User = "admin", Authentication = SshAuthentication.Password };
        var secrets = new InMemorySecretStore();
        var ssh = new FakeSshClient { Result = new SshResult(0, SampleOutput, string.Empty, TimeSpan.Zero) };
        var probe = new ServerProbe(ssh, secrets);

        var missing = await Assert.ThrowsAsync<ProbeException>(() => probe.ProbeAsync(host, HostKeyPolicy.Strict, CancellationToken.None));
        Assert.Equal(ProbeFailureKind.AuthenticationFailed, missing.Kind);
        Assert.Empty(ssh.Calls);

        secrets.Write(host.SecretKey, "stored");
        await probe.ProbeAsync(host, HostKeyPolicy.Strict, CancellationToken.None);
        await probe.ProbeAsync(host, HostKeyPolicy.AcceptNew, CancellationToken.None, "typed");

        Assert.Equal(["stored", "typed"], ssh.Targets.Select(target => target.Password));
        Assert.Equal($"server/{host.Id}", host.SecretKey);
    }

    [Fact]
    public async Task Probe_NeverHandsAPasswordToKeyBasedSignIn()
    {
        var secrets = new InMemorySecretStore();
        secrets.Write(Host.SecretKey, "leftover");
        var ssh = new FakeSshClient { Result = new SshResult(0, SampleOutput, string.Empty, TimeSpan.Zero) };

        await new ServerProbe(ssh, secrets).ProbeAsync(Host, HostKeyPolicy.Strict, CancellationToken.None, "typed");

        Assert.Null(Assert.Single(ssh.Targets).Password);
    }

    [Theory]
    [InlineData("-oProxyCommand=calc.exe", null)]
    [InlineData("host name", null)]
    [InlineData("", null)]
    [InlineData("build-01", "-evil")]
    [InlineData("build-01", "a b")]
    public void SshArguments_RejectValuesThatCouldBeReadAsOptions(string address, string? user)
    {
        var target = new SshTarget(address, user, null, SshAuthentication.Agent, null);

        Assert.Throws<ArgumentException>(() => OpenSshClient.BuildArguments(target, HostKeyPolicy.Strict, "uptime"));
    }

    [Fact]
    public void SshTarget_ValidatesPortAndKeyFile()
    {
        Assert.Throws<ArgumentException>(() => new SshTarget("host", null, 70000, SshAuthentication.Agent, null).Validate());
        Assert.Throws<ArgumentException>(() => new SshTarget("host", null, null, SshAuthentication.KeyFile, " ").Validate());
        new SshTarget("2001:db8::1", "deploy", 22, SshAuthentication.Agent, null).Validate();
    }

    [Fact]
    public void SshConfig_ListsConcreteHostsOnly()
    {
        string[] lines =
        [
            "# work",
            "Host dev-vm nas",
            "    HostName 10.0.0.5",
            "    User deploy",
            "host=build-*",
            "Host *",
            "    ServerAliveInterval 30",
            "Host !bastion jump",
            "Match host example.com",
            "Host dev-vm",
        ];

        Assert.Equal(["dev-vm", "nas", "jump"], SshConfigFile.ParseHostAliases(lines));
        Assert.Empty(SshConfigFile.ReadHostAliases(@"C:\does\not\exist\config"));
    }
}
