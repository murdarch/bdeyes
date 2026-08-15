using System.Text.Json;

namespace Bdeyes.Services;

public sealed record UserSettings(string? LastWorkspace = null);

public sealed class UserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public UserSettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "bdeyes",
            "settings.json");
    }

    public async Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new UserSettings();
            }

            await using var stream = File.OpenRead(_settingsPath);
            return await JsonSerializer.DeserializeAsync<UserSettings>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? new UserSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UserSettings();
        }
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("The settings path has no parent folder.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
