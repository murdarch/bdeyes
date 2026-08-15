using Bdeyes.Models;
using Bdeyes.Services;
using Bdeyes.ViewModels;

namespace Bdeyes.Tests;

public sealed class BdSettingsTests
{
    [Fact]
    public void AppExposesThePreviewVersion()
    {
        var viewModel = new MainViewModel();

        Assert.Equal("bdeyes 0.1.0-preview.1", viewModel.AppVersionLabel);
    }

    [Fact]
    public async Task TestedExecutableIsPersistedAndReused()
    {
        var root = TestRoot();
        var automatic = Path.Combine(root, "automatic", ExecutableName());
        var selected = Path.Combine(root, "selected", ExecutableName());
        var settingsPath = Path.Combine(root, Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var locator = Locator(Path.GetDirectoryName(automatic)!, automatic, selected);
            var client = new ConfigurableStubBdClient();
            var store = new UserSettingsStore(settingsPath);
            var viewModel = new MainViewModel(client, store, null, locator);
            await viewModel.InitializeAsync();

            Assert.Equal(Path.GetFullPath(automatic), client.Executable);

            viewModel.OpenBdSettingsCommand.Execute(null);
            viewModel.BdExecutableDraft = selected;
            await viewModel.TestBdExecutableCommand.ExecuteAsync(null);

            Assert.True(viewModel.BdExecutableTestSucceeded);
            Assert.Equal("bd 1.1.2", viewModel.BdExecutableVersionLabel);
            Assert.Equal(Path.GetFullPath(automatic), client.Executable);

            await viewModel.SaveBdSettingsCommand.ExecuteAsync(null);

            Assert.Equal(Path.GetFullPath(selected), client.Executable);
            Assert.Equal(Path.GetFullPath(selected), (await store.LoadAsync()).BdExecutablePath);

            var restoredClient = new ConfigurableStubBdClient();
            var restored = new MainViewModel(restoredClient, store, null, locator);
            await restored.InitializeAsync();

            Assert.Equal(Path.GetFullPath(selected), restoredClient.Executable);
            Assert.False(restored.IsBdExecutableAutomatic);
        }
        finally
        {
            DeleteSettingsDirectory(settingsPath);
        }
    }

    [Fact]
    public async Task ResetReturnsToAutomaticDiscoveryAndRemovesTheSavedPath()
    {
        var root = TestRoot();
        var automatic = Path.Combine(root, "automatic", ExecutableName());
        var selected = Path.Combine(root, "selected", ExecutableName());
        var settingsPath = Path.Combine(root, Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var locator = Locator(Path.GetDirectoryName(automatic)!, automatic, selected);
            var store = new UserSettingsStore(settingsPath);
            await store.SaveAsync(new UserSettings(BdExecutablePath: selected));
            var client = new ConfigurableStubBdClient();
            var viewModel = new MainViewModel(client, store, null, locator);
            await viewModel.InitializeAsync();
            viewModel.OpenBdSettingsCommand.Execute(null);

            viewModel.ResetBdExecutableCommand.Execute(null);
            await viewModel.TestBdExecutableCommand.ExecuteAsync(null);
            await viewModel.SaveBdSettingsCommand.ExecuteAsync(null);

            Assert.True(viewModel.IsBdExecutableAutomatic);
            Assert.Equal(Path.GetFullPath(automatic), client.Executable);
            Assert.Null((await store.LoadAsync()).BdExecutablePath);
        }
        finally
        {
            DeleteSettingsDirectory(settingsPath);
        }
    }

    [Fact]
    public async Task InvalidSelectionCannotBeSaved()
    {
        var root = TestRoot();
        var automatic = Path.Combine(root, "automatic", ExecutableName());
        var settingsPath = Path.Combine(root, Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var locator = Locator(Path.GetDirectoryName(automatic)!, automatic);
            var viewModel = new MainViewModel(
                new ConfigurableStubBdClient(),
                new UserSettingsStore(settingsPath),
                null,
                locator);
            await viewModel.InitializeAsync();
            viewModel.OpenBdSettingsCommand.Execute(null);
            viewModel.BdExecutableDraft = Path.Combine(root, "missing", ExecutableName());

            await viewModel.TestBdExecutableCommand.ExecuteAsync(null);

            Assert.False(viewModel.BdExecutableTestSucceeded);
            Assert.Contains("does not exist", viewModel.BdExecutableStatusMessage);
            Assert.False(viewModel.SaveBdSettingsCommand.CanExecute(null));
        }
        finally
        {
            DeleteSettingsDirectory(settingsPath);
        }
    }

    private static BdExecutableLocator Locator(string searchPath, params string[] existingFiles)
    {
        var files = existingFiles
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var context = new BdDiscoveryContext(
            null,
            searchPath,
            ".COM;.EXE;.BAT;.CMD",
            Path.Combine(TestRoot(), "home"),
            Path.Combine(TestRoot(), "local"),
            OperatingSystem.IsWindows(),
            OperatingSystem.IsMacOS());
        return new BdExecutableLocator(context, files.Contains);
    }

    private static string ExecutableName() => OperatingSystem.IsWindows() ? "bd.cmd" : "bd";

    private static string TestRoot() => Path.Combine(Path.GetTempPath(), "bdeyes-settings-tests");

    private static void DeleteSettingsDirectory(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ConfigurableStubBdClient : IConfigurableBdClient
    {
        public string Executable { get; private set; } = "bd";

        public void ConfigureExecutable(string executable) => Executable = executable;

        public Task<string> ProbeVersionAsync(
            string executable,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("bd version 1.1.2 (test)");

        public Task<BdWorkspaceSnapshot> LoadWorkspaceAsync(
            string workspacePath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No workspace should be loaded in this test.");

        public Task<BeadIssue> LoadDetailAsync(
            string workspacePath,
            string issueId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No detail should be loaded in this test.");
    }
}
