namespace Bdeyes.Services;

public enum BdExecutableSource
{
    Saved,
    Environment,
    Path,
    KnownLocation,
    Missing,
}

public sealed record BdDiscoveryContext(
    string? EnvironmentOverride,
    string SearchPath,
    string PathExtensions,
    string UserProfile,
    string LocalApplicationData,
    bool IsWindows,
    bool IsMacOS)
{
    public static BdDiscoveryContext Capture() => new(
        Environment.GetEnvironmentVariable("BDEYES_BD"),
        Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
        Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD",
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        OperatingSystem.IsWindows(),
        OperatingSystem.IsMacOS());
}

public sealed record BdExecutableResolution(
    string Executable,
    BdExecutableSource Source,
    string? Warning = null)
{
    public bool IsFound => Source != BdExecutableSource.Missing;

    public string SourceLabel => Source switch
    {
        BdExecutableSource.Saved => "Saved path",
        BdExecutableSource.Environment => "BDEYES_BD",
        BdExecutableSource.Path => "System PATH",
        BdExecutableSource.KnownLocation => "Standard install location",
        _ => "Not found",
    };
}

public sealed class BdExecutableLocator
{
    private readonly BdDiscoveryContext _context;
    private readonly Func<string, bool> _fileExists;

    public BdExecutableLocator()
        : this(BdDiscoveryContext.Capture(), File.Exists)
    {
    }

    public BdExecutableLocator(
        BdDiscoveryContext context,
        Func<string, bool>? fileExists = null)
    {
        _context = context;
        _fileExists = fileExists ?? File.Exists;
    }

    public BdExecutableResolution Resolve(string? savedPath)
    {
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(savedPath))
        {
            var saved = ResolveCandidate(savedPath);
            if (saved is not null)
            {
                return new BdExecutableResolution(saved, BdExecutableSource.Saved);
            }

            warnings.Add("The saved bd executable is no longer available.");
        }

        if (!string.IsNullOrWhiteSpace(_context.EnvironmentOverride))
        {
            var environment = ResolveCandidate(_context.EnvironmentOverride);
            if (environment is not null)
            {
                return new BdExecutableResolution(
                    environment,
                    BdExecutableSource.Environment,
                    JoinWarnings(warnings));
            }

            warnings.Add("BDEYES_BD does not resolve to an executable file.");
        }

        var fromPath = FindOnPath("bd");
        if (fromPath is not null)
        {
            return new BdExecutableResolution(
                fromPath,
                BdExecutableSource.Path,
                JoinWarnings(warnings));
        }

        foreach (var candidate in KnownLocations())
        {
            var known = NormalizeExistingFile(candidate);
            if (known is not null)
            {
                return new BdExecutableResolution(
                    known,
                    BdExecutableSource.KnownLocation,
                    JoinWarnings(warnings));
            }
        }

        warnings.Add("Install bd or choose its executable in Settings.");
        return new BdExecutableResolution(
            "bd",
            BdExecutableSource.Missing,
            JoinWarnings(warnings));
    }

    public BdExecutableResolution ResolveExplicit(string executable)
    {
        var resolved = ResolveCandidate(executable);
        return resolved is not null
            ? new BdExecutableResolution(resolved, BdExecutableSource.Saved)
            : new BdExecutableResolution(
                executable.Trim(),
                BdExecutableSource.Missing,
                "That file does not exist or is not discoverable on PATH.");
    }

    private string? ResolveCandidate(string candidate)
    {
        var value = candidate.Trim().Trim('"');
        if (value.Length == 0)
        {
            return null;
        }

        return HasDirectoryComponent(value)
            ? NormalizeExistingFile(value)
            : FindOnPath(value);
    }

    private string? FindOnPath(string command)
    {
        foreach (var directory in _context.SearchPath.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var fileName in CandidateFileNames(command))
            {
                var resolved = NormalizeExistingFile(Path.Combine(directory, fileName));
                if (resolved is not null)
                {
                    return resolved;
                }
            }
        }

        return null;
    }

    private IEnumerable<string> CandidateFileNames(string command)
    {
        yield return command;
        if (!_context.IsWindows || Path.HasExtension(command))
        {
            yield break;
        }

        foreach (var extension in _context.PathExtensions.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return $"{command}{extension.ToLowerInvariant()}";
        }
    }

    private IEnumerable<string> KnownLocations()
    {
        if (_context.IsWindows)
        {
            yield return Path.Combine(
                _context.LocalApplicationData,
                "Programs",
                "bd",
                "bd.exe");
            yield return Path.Combine(_context.UserProfile, ".local", "bin", "bd.exe");
            yield break;
        }

        yield return Path.Combine(_context.UserProfile, ".local", "bin", "bd");
        yield return "/usr/local/bin/bd";
        if (_context.IsMacOS)
        {
            yield return "/opt/homebrew/bin/bd";
        }
    }

    private string? NormalizeExistingFile(string candidate)
    {
        try
        {
            var fullPath = Path.GetFullPath(candidate);
            return _fileExists(fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool HasDirectoryComponent(string value) =>
        Path.IsPathRooted(value) ||
        value.Contains(Path.DirectorySeparatorChar) ||
        value.Contains(Path.AltDirectorySeparatorChar);

    private static string? JoinWarnings(IEnumerable<string> warnings)
    {
        var message = string.Join(" ", warnings);
        return message.Length == 0 ? null : message;
    }
}
