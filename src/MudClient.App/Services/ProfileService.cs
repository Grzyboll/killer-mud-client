using System.IO;
using System.Text.Json;
using MudClient.App.Models;

namespace MudClient.App.Services;

/// <summary>
/// Stores user profiles as JSON files, one file per profile.
/// Default location: %AppData%\KillerMudClient\Profiles.
/// </summary>
public sealed class ProfileService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>File (without extension) holding globally shared rules/timers/locations.</summary>
    private const string GlobalFileName = "_global";

    private readonly string _directory;

    public ProfileService(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KillerMudClient",
            "Profiles");
    }

    public IReadOnlyList<string> ListProfileNames()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(_directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Where(name => !string.Equals(name, GlobalFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool Exists(string name) => File.Exists(GetPath(name));

    /// <summary>Removes the account's file from disk. No-op when it doesn't exist.</summary>
    public void Delete(string name)
    {
        var path = GetPath(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public ProfileData? Load(string name)
    {
        var path = GetPath(name);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var profile = JsonSerializer.Deserialize<ProfileData>(json, SerializerOptions);
            if (profile is not null)
            {
                profile.Name = name;
            }

            return profile;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    public void Save(ProfileData profile)
    {
        Directory.CreateDirectory(_directory);
        var json = JsonSerializer.Serialize(profile, SerializerOptions);
        File.WriteAllText(GetPath(profile.Name), json);
    }

    /// <summary>
    /// Last-write timestamp of a profile's file, or null if it doesn't exist yet.
    /// Lets a caller detect that the file changed on disk since it last loaded or
    /// saved it — e.g. another running instance of the client saved the same
    /// profile in the meantime — without having to read and compare its content.
    /// </summary>
    public DateTime? GetLastWriteTimeUtc(string name)
    {
        var path = GetPath(name);
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }

    public GlobalData LoadGlobal()
    {
        var path = Path.Combine(_directory, GlobalFileName + ".json");
        if (!File.Exists(path))
        {
            return new GlobalData();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<GlobalData>(json, SerializerOptions) ?? new GlobalData();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new GlobalData();
        }
    }

    public void SaveGlobal(GlobalData data)
    {
        Directory.CreateDirectory(_directory);
        var json = JsonSerializer.Serialize(data, SerializerOptions);
        File.WriteAllText(Path.Combine(_directory, GlobalFileName + ".json"), json);
    }

    /// <summary>Same as <see cref="GetLastWriteTimeUtc"/>, but for the shared global file.</summary>
    public DateTime? GetGlobalLastWriteTimeUtc()
    {
        var path = Path.Combine(_directory, GlobalFileName + ".json");
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }

    private string GetPath(string name) => Path.Combine(_directory, Sanitize(name) + ".json");

    /// <summary>
    /// Turns a profile name into a safe file name (profile names come from user input).
    /// </summary>
    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(chars);

        // A profile must never overwrite the shared global file.
        return string.Equals(sanitized, GlobalFileName, StringComparison.OrdinalIgnoreCase)
            ? sanitized + "_profil"
            : sanitized;
    }
}
