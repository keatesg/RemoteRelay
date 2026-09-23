using System;
using System.IO;
using RemoteRelay.Common;
using Xunit;

namespace RemoteRelay.Tests;

public class AppPathsTests
{
    [Fact]
    public void AppPaths_ServerConfigPath_EndsWithConfigJson()
    {
        var path = AppPaths.ServerConfigPath;
        Assert.NotNull(path);
        Assert.EndsWith("config.json", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppPaths_ClientConfigPath_EndsWithClientConfigJson()
    {
        var path = AppPaths.ClientConfigPath;
        Assert.NotNull(path);
        Assert.EndsWith("ClientConfig.json", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppPaths_ResolveDataDir_ReturnsNonEmptyString()
    {
        var serverDir = AppPaths.ResolveDataDir(AppPaths.ServerComponent);
        var clientDir = AppPaths.ResolveDataDir(AppPaths.ClientComponent);

        Assert.False(string.IsNullOrWhiteSpace(serverDir));
        Assert.False(string.IsNullOrWhiteSpace(clientDir));
    }
}
