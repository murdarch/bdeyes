using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Bdeyes.Models;

namespace Bdeyes.Services;

public interface IBdClient
{
    Task<BdWorkspaceSnapshot> LoadWorkspaceAsync(string workspacePath, CancellationToken cancellationToken = default);

    Task<BeadIssue> LoadDetailAsync(string workspacePath, string issueId, CancellationToken cancellationToken = default);
}

public sealed class BdClient : IBdClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly string _executable;
    private readonly TimeProvider _timeProvider;

    public BdClient(string? executable = null, TimeProvider? timeProvider = null)
    {
        _executable = string.IsNullOrWhiteSpace(executable)
            ? Environment.GetEnvironmentVariable("BDEYES_BD") ?? "bd"
            : executable;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BdWorkspaceSnapshot> LoadWorkspaceAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ValidateWorkspace(workspacePath);
        var issuesTask = RunAsync(BdCommandFactory.ListIssues(fullPath), cancellationToken);
        var versionTask = RunAsync(BdCommandFactory.Version(fullPath), cancellationToken);
        await Task.WhenAll(issuesTask, versionTask).ConfigureAwait(false);

        IReadOnlyList<BeadIssue> issues;
        try
        {
            issues = JsonSerializer.Deserialize<List<BeadIssue>>(await issuesTask, JsonOptions) ?? [];
        }
        catch (JsonException exception)
        {
            throw new BdClientException("bd returned issue data that bdeyes could not read.", exception);
        }

        return new BdWorkspaceSnapshot(
            fullPath,
            (await versionTask).Trim(),
            _timeProvider.GetUtcNow(),
            issues);
    }

    public async Task<BeadIssue> LoadDetailAsync(
        string workspacePath,
        string issueId,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ValidateWorkspace(workspacePath);
        if (string.IsNullOrWhiteSpace(issueId))
        {
            throw new ArgumentException("An issue id is required.", nameof(issueId));
        }

        var output = await RunAsync(BdCommandFactory.ShowIssue(fullPath, issueId), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var issues = JsonSerializer.Deserialize<List<BeadIssue>>(output, JsonOptions);
            return issues is { Count: > 0 }
                ? issues[0]
                : throw new BdClientException($"bd did not return issue {issueId}.");
        }
        catch (JsonException exception)
        {
            throw new BdClientException($"bd returned unreadable detail for {issueId}.", exception);
        }
    }

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(arguments),
        };

        try
        {
            if (!process.Start())
            {
                throw new BdClientException("The bd process did not start.");
            }
        }
        catch (Exception exception) when (exception is not BdClientException)
        {
            throw new BdClientException(
                $"Could not start '{_executable}'. Install bd or set BDEYES_BD to its path.",
                exception);
        }

        try
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(error) ? output : error;
                throw new BdClientException(
                    string.IsNullOrWhiteSpace(message)
                        ? $"bd exited with code {process.ExitCode}."
                        : message.Trim());
            }

            return output;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
    }

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        if (OperatingSystem.IsWindows())
        {
            var encodedArguments = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(arguments)));
            var startInfo = BaseStartInfo("powershell.exe");
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                "$ErrorActionPreference='Stop'; " +
                "$json=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:BDEYES_ARGUMENTS)); " +
                "$arguments=@(ConvertFrom-Json $json); " +
                "& $env:BDEYES_BD @arguments; exit $LASTEXITCODE");
            startInfo.Environment["BDEYES_BD"] = _executable;
            startInfo.Environment["BDEYES_ARGUMENTS"] = encodedArguments;
            return startInfo;
        }

        var directStartInfo = BaseStartInfo(_executable);
        foreach (var argument in arguments)
        {
            directStartInfo.ArgumentList.Add(argument);
        }

        return directStartInfo;
    }

    private static ProcessStartInfo BaseStartInfo(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    private static string ValidateWorkspace(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            throw new ArgumentException("A workspace folder is required.", nameof(workspacePath));
        }

        var fullPath = Path.GetFullPath(workspacePath);
        return Directory.Exists(fullPath)
            ? fullPath
            : throw new DirectoryNotFoundException($"Workspace folder not found: {fullPath}");
    }
}

public static class BdCommandFactory
{
    public static IReadOnlyList<string> ListIssues(string workspacePath) =>
    [
        "--readonly",
        "-C",
        workspacePath,
        "list",
        "--all",
        "--limit",
        "0",
        "--flat",
        "--json",
    ];

    public static IReadOnlyList<string> ShowIssue(string workspacePath, string issueId) =>
    [
        "--readonly",
        "-C",
        workspacePath,
        "show",
        issueId,
        "--include-comments",
        "--include-dependents",
        "--json",
    ];

    public static IReadOnlyList<string> Version(string workspacePath) =>
    [
        "--readonly",
        "-C",
        workspacePath,
        "version",
    ];
}

public sealed class BdClientException : Exception
{
    public BdClientException(string message)
        : base(message)
    {
    }

    public BdClientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
