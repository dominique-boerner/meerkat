using Meerkat.Widgets.Docker.Containers;

namespace Meerkat.Tests.Docker;

public class DockerOutputParserTests
{
    [Fact]
    public void ParseContainerList_ReadsOneContainerPerJsonLine()
    {
        const string output = """
            {"Command":"\"docker-entrypoint.s…\"","CreatedAt":"2026-11-20 09:12:01 +0100 CET","ID":"3f2a9c1d7b6e","Image":"postgres:17","Names":"db","State":"running","Status":"Up 2 hours"}
            {"ID":"a1b2c3d4e5f6","Image":"grafana/grafana:latest","Names":"grafana-ü","State":"exited","Status":"Exited (0) 3 days ago"}

            """;

        var containers = DockerOutputParser.ParseContainerList(output);

        Assert.Equal(
            [
                new ContainerInfo("3f2a9c1d7b6e", "db", "postgres:17", "running", "Up 2 hours"),
                new ContainerInfo("a1b2c3d4e5f6", "grafana-ü", "grafana/grafana:latest", "exited", "Exited (0) 3 days ago"),
            ],
            containers);
    }

    [Fact]
    public void ParseContainerList_SkipsLinesThatAreNotJson()
    {
        const string output = "WARNING: something happened\n{\"ID\":\"abc\",\"Names\":\"web\"}\n{broken";

        var containers = DockerOutputParser.ParseContainerList(output);

        Assert.Equal("web", Assert.Single(containers).Name);
    }

    [Fact]
    public void ParseStats_ReadsCpuAndMemory()
    {
        const string output = """
            {"BlockIO":"0B / 0B","CPUPerc":"12.34%","Container":"3f2a9c1d7b6e","ID":"3f2a9c1d7b6e","MemPerc":"1.56%","MemUsage":"120.5MiB / 7.675GiB","Name":"db","NetIO":"1kB / 0B","PIDs":"7"}
            """;

        var stats = Assert.Single(DockerOutputParser.ParseStats(output));

        Assert.Equal("3f2a9c1d7b6e", stats.Id);
        Assert.Equal("db", stats.Name);
        Assert.Equal(12.34, stats.CpuPercent);
        Assert.Equal(1.56, stats.MemoryPercent);
        Assert.Equal(126_353_408, stats.MemoryUsageBytes);
        Assert.Equal(8_240_968_499, stats.MemoryLimitBytes);
    }

    [Fact]
    public void ParseStats_ToleratesPlaceholderValues()
    {
        var stats = Assert.Single(DockerOutputParser.ParseStats("""{"ID":"abc","CPUPerc":"--","MemUsage":"-- / --","MemPerc":"--"}"""));

        Assert.Null(stats.CpuPercent);
        Assert.Null(stats.MemoryUsageBytes);
        Assert.Null(stats.MemoryLimitBytes);
        Assert.Null(stats.MemoryPercent);
    }

    [Theory]
    [InlineData("0B", 0L)]
    [InlineData("512B", 512L)]
    [InlineData("1.5KiB", 1536L)]
    [InlineData("2MiB", 2_097_152L)]
    [InlineData("1GiB", 1_073_741_824L)]
    [InlineData("1kB", 1000L)]
    [InlineData("3MB", 3_000_000L)]
    [InlineData(" 4GB ", 4_000_000_000L)]
    public void ParseByteSize_UnderstandsBinaryAndDecimalUnits(string text, long expected)
    {
        Assert.Equal(expected, DockerOutputParser.ParseByteSize(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--")]
    [InlineData("12 parsecs")]
    public void ParseByteSize_ReturnsNullForUnknownInput(string? text)
    {
        Assert.Null(DockerOutputParser.ParseByteSize(text));
    }
}
