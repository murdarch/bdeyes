using Bdeyes.Services;

namespace Bdeyes.Tests;

public sealed class BdExecutableLocatorTests
{
    [Fact]
    public void SavedPathWinsOverEnvironmentAndPath()
    {
        var root = TestRoot();
        var saved = Path.Combine(root, "saved", ExecutableName());
        var environment = Path.Combine(root, "environment", ExecutableName());
        var onPath = Path.Combine(root, "path", ExecutableName());
        var locator = Locator(
            environment,
            Path.GetDirectoryName(onPath)!,
            saved,
            environment,
            onPath);

        var resolution = locator.Resolve(saved);

        Assert.Equal(BdExecutableSource.Saved, resolution.Source);
        Assert.Equal(Path.GetFullPath(saved), resolution.Executable);
        Assert.Null(resolution.Warning);
    }

    [Fact]
    public void MissingSavedPathFallsBackAndKeepsAnActionableWarning()
    {
        var root = TestRoot();
        var missing = Path.Combine(root, "missing", ExecutableName());
        var environment = Path.Combine(root, "environment", ExecutableName());
        var locator = Locator(environment, string.Empty, environment);

        var resolution = locator.Resolve(missing);

        Assert.Equal(BdExecutableSource.Environment, resolution.Source);
        Assert.Equal(Path.GetFullPath(environment), resolution.Executable);
        Assert.Contains("saved bd executable", resolution.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PathDiscoveryReturnsTheResolvedFileRatherThanABareCommand()
    {
        var root = TestRoot();
        var onPath = Path.Combine(root, "path", ExecutableName());
        var locator = Locator(null, Path.GetDirectoryName(onPath)!, onPath);

        var resolution = locator.Resolve(null);

        Assert.Equal(BdExecutableSource.Path, resolution.Source);
        Assert.Equal(Path.GetFullPath(onPath), resolution.Executable);
    }

    [Fact]
    public void MissingDiscoveryExplainsHowToRecover()
    {
        var locator = Locator(null, string.Empty);

        var resolution = locator.Resolve(null);

        Assert.Equal(BdExecutableSource.Missing, resolution.Source);
        Assert.Equal("bd", resolution.Executable);
        Assert.Contains("choose", resolution.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitSelectionMustResolveToARealFile()
    {
        var locator = Locator(null, string.Empty);

        var resolution = locator.ResolveExplicit(Path.Combine(TestRoot(), "absent", ExecutableName()));

        Assert.Equal(BdExecutableSource.Missing, resolution.Source);
        Assert.Contains("does not exist", resolution.Warning, StringComparison.OrdinalIgnoreCase);
    }

    private static BdExecutableLocator Locator(
        string? environmentOverride,
        string searchPath,
        params string[] existingFiles)
    {
        var files = existingFiles
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var context = new BdDiscoveryContext(
            environmentOverride,
            searchPath,
            ".COM;.EXE;.BAT;.CMD",
            Path.Combine(TestRoot(), "home"),
            Path.Combine(TestRoot(), "local"),
            OperatingSystem.IsWindows(),
            OperatingSystem.IsMacOS());
        return new BdExecutableLocator(context, files.Contains);
    }

    private static string ExecutableName() => OperatingSystem.IsWindows() ? "bd.cmd" : "bd";

    private static string TestRoot() => Path.Combine(Path.GetTempPath(), "bdeyes-locator-tests");
}
