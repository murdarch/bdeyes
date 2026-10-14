using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Bdeyes.Models;

namespace Bdeyes.Services;

public interface IBdClient
{
    Task<BdWorkspaceSnapshot> LoadWorkspaceAsync(string workspacePath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BdMemoryEntry>> LoadMemoriesAsync(
        string workspacePath,
        CancellationToken cancellationToken = default);


    Task<BeadIssue> LoadDetailAsync(string workspacePath, string issueId, CancellationToken cancellationToken = default);
}

public interface IConfigurableBdClient : IBdClient
{
    string Executable { get; }

    void ConfigureExecutable(string executable);

    Task<string> ProbeVersionAsync(
        string executable,
        CancellationToken cancellationToken = default);
}

public interface IBdEventClient
{
    Task<BdEventBaseline> ReadEventBaselineAsync(
        string workspacePath,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<BdEventRecord> WatchEventsAsync(
        string workspacePath,
        long sequence,
        CancellationToken cancellationToken = default);
}

public sealed class BdClient : IConfigurableBdClient, IBdEventClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private string _executable;
    private readonly TimeProvider _timeProvider;

    public BdClient(string? executable = null, TimeProvider? timeProvider = null)
    {
        _executable = string.IsNullOrWhiteSpace(executable)
            ? Environment.GetEnvironmentVariable("BDEYES_BD") ?? "bd"
            : executable;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Executable => _executable;

    public void ConfigureExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new ArgumentException("A bd executable is required.", nameof(executable));
        }

        _executable = executable;
    }

    public async Task<string> ProbeVersionAsync(
        string executable,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new ArgumentException("A bd executable is required.", nameof(executable));
        }

        var output = (await RunAsync(
                executable,
                BdCommandFactory.ProbeVersion(),
                cancellationToken)
            .ConfigureAwait(false)).Trim();
        if (!output.StartsWith("bd version ", StringComparison.OrdinalIgnoreCase))
        {
            throw new BdClientException("The selected file did not identify itself as bd.");
        }

        return output;
    }

    public async Task<BdWorkspaceSnapshot> LoadWorkspaceAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ValidateWorkspace(workspacePath);
        var issuesTask = RunAsync(BdCommandFactory.ListIssues(fullPath), cancellationToken);
        var versionTask = RunAsync(BdCommandFactory.Version(fullPath), cancellationToken);
        await Task.WhenAll(issuesTask, versionTask).ConfigureAwait(false);

        var issuesJson = await issuesTask;
        IReadOnlyList<BeadIssue> issues;
        try
        {
            issues = JsonSerializer.Deserialize<List<BeadIssue>>(issuesJson, JsonOptions) ?? [];
        }
        catch (JsonException exception)
        {
            throw new BdClientException("bd returned issue data that bdeyes could not read.", exception);
        }

        return new BdWorkspaceSnapshot(
            fullPath,
            (await versionTask).Trim(),
            _timeProvider.GetUtcNow(),
            issues,
            ContentRevisionOf(issuesJson));
    }
    public async Task<IReadOnlyList<BdMemoryEntry>> LoadMemoriesAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ValidateWorkspace(workspacePath);
        var output = await RunAsync(BdCommandFactory.ListMemories(fullPath), cancellationToken)
            .ConfigureAwait(false);
        return ParseMemories(output);
    }

    internal static IReadOnlyList<BdMemoryEntry> ParseMemories(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new BdClientException("bd returned memory data in an unexpected shape.");
            }

            var memories = new List<BdMemoryEntry>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("schema_version"))
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new BdClientException(
                        $"bd returned a non-text value for memory '{property.Name}'.");
                }

                memories.Add(new BdMemoryEntry(
                    property.Name,
                    property.Value.GetString() ?? string.Empty));
            }

            return memories;
        }
        catch (JsonException exception)
        {
            throw new BdClientException("bd returned memory data that bdeyes could not read.", exception);
        }
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

    public async Task<BdEventBaseline> ReadEventBaselineAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ValidateWorkspace(workspacePath);
        using var process = StartProcess(
            _executable,
            BdCommandFactory.EventBaseline(fullPath));
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        var nonEventOutput = new StringBuilder();
        var sequence = 0L;

        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken)
                   is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (TryParseEventLine(line, out var record))
                {
                    sequence = Math.Max(sequence, record.Sequence);
                }
                else
                {
                    nonEventOutput.AppendLine(line);
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            if (error.Contains(
                    "events journal is disabled",
                    StringComparison.OrdinalIgnoreCase))
            {
                return BdEventBaseline.Unavailable("The events journal is disabled.");
            }

            if (process.ExitCode == 0)
            {
                return BdEventBaseline.Available(sequence);
            }

            var diagnostic = string.Concat(nonEventOutput, error);
            return TryReadTruncatedHead(diagnostic, out var head)
                ? BdEventBaseline.Available(head)
                : BdEventBaseline.Unavailable(FirstDiagnosticLine(
                    diagnostic,
                    $"bd events exited with code {process.ExitCode}."));
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

    public async IAsyncEnumerable<BdEventRecord> WatchEventsAsync(
        string workspacePath,
        long sequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var fullPath = ValidateWorkspace(workspacePath);
        using var process = StartProcess(
            _executable,
            BdCommandFactory.WatchEvents(fullPath, sequence));
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken)
                   is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (!TryParseEventLine(line, out var record))
                {
                    if (TryReadTruncatedHead(line, out _))
                    {
                        throw new BdClientException(
                            "The events journal checkpoint was truncated; a full refresh is required.");
                    }

                    throw new BdClientException(
                        "bd returned an unreadable events-journal record.");
                }

                yield return record;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            throw new BdClientException(FirstDiagnosticLine(
                error,
                process.ExitCode == 0
                    ? "The bd events stream ended unexpectedly."
                    : $"bd events exited with code {process.ExitCode}."));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    internal static bool TryParseEventLine(string line, out BdEventRecord record)
    {
        record = null!;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("code", out _))
            {
                return false;
            }

            var parsed = document.RootElement.Deserialize<BdEventRecord>(JsonOptions);
            if (parsed is null ||
                parsed.Sequence <= 0 ||
                string.IsNullOrWhiteSpace(parsed.Operation) ||
                string.IsNullOrWhiteSpace(parsed.IssueId))
            {
                return false;
            }

            record = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadTruncatedHead(string content, out long head)
    {
        head = 0;
        try
        {
            using var document = JsonDocument.Parse(content.Trim());
            var root = document.RootElement;
            return root.TryGetProperty("code", out var code) &&
                string.Equals(
                    code.GetString(),
                    "events_journal_truncated",
                    StringComparison.Ordinal) &&
                root.TryGetProperty("head", out var value) &&
                value.TryGetInt64(out head);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string FirstDiagnosticLine(string content, string fallback) =>
        content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ??
        fallback;

    private Task<string> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        RunAsync(_executable, arguments, cancellationToken);

    private async Task<string> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = StartProcess(executable, arguments);

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

    private Process StartProcess(
        string executable,
        IReadOnlyList<string> arguments)
    {
        var process = new Process
        {
            StartInfo = CreateStartInfo(executable, arguments),
        };

        try
        {
            if (!process.Start())
            {
                throw new BdClientException("The bd process did not start.");
            }

            return process;
        }
        catch (Exception exception) when (exception is not BdClientException)
        {
            process.Dispose();
            throw new BdClientException(
                $"Could not start '{executable}'. Install bd or choose its executable in Settings.",
                exception);
        }
    }

    private ProcessStartInfo CreateStartInfo(
        string executable,
        IReadOnlyList<string> arguments)
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
            startInfo.Environment["BDEYES_BD"] = executable;
            startInfo.Environment["BDEYES_ARGUMENTS"] = encodedArguments;
            return startInfo;
        }

        var directStartInfo = BaseStartInfo(executable);
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

    private static WorkspaceContentRevision ContentRevisionOf(string content)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var fingerprint = offsetBasis;

        unchecked
        {
            foreach (var character in content)
            {
                fingerprint ^= (byte)character;
                fingerprint *= prime;
                fingerprint ^= (byte)(character >> 8);
                fingerprint *= prime;
            }
        }

        return new WorkspaceContentRevision(content.Length, fingerprint);
    }

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
    public static IReadOnlyList<string> ListMemories(string workspacePath) =>
    [
        "--readonly",
        "-C",
        workspacePath,
        "memories",
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

    public static IReadOnlyList<string> EventBaseline(string workspacePath) =>
    [
        "--readonly",
        "-C",
        workspacePath,
        "--json",
        "events",
        "tail",
        "--since",
        "0",
    ];

    public static IReadOnlyList<string> WatchEvents(string workspacePath, long sequence) =>
    [
        "--readonly",
        "-C",
        workspacePath,
        "--json",
        "events",
        "tail",
        "--since",
        sequence.ToString(CultureInfo.InvariantCulture),
        "--follow",
    ];

    public static IReadOnlyList<string> Version(string workspacePath) =>
    [
        "--readonly",
        "-C",
        workspacePath,
        "version",
    ];
    public static IReadOnlyList<string> ProbeVersion() =>
    [
        "--readonly",
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
