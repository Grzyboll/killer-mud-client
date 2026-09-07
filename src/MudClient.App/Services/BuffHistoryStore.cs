using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MudClient.App.Models;
using MudClient.Core.BuffTimers;

namespace MudClient.App.Services;

public sealed class BuffHistoryStore
{
    private const int MaximumMeasurementsPerBuff = 1000;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly string _directory;

    public BuffHistoryStore(string? settingsDirectory = null)
    {
        var root = settingsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KillerMudClient");
        _directory = Path.Combine(root, "BuffTimers");
    }

    public BuffHistoryDocument Load(BuffCharacterKey character)
    {
        var path = GetPath(character);
        if (!DurableJsonFile.TryRead<BuffHistoryDocument>(path, SerializerOptions, out var document)
            || document is null
            || document.SchemaVersion > BuffHistoryDocument.CurrentSchemaVersion)
        {
            return NewDocument(character);
        }

        var loadedSchemaVersion = document.SchemaVersion;
        document.Character = character;
        document.Measurements ??= [];
        document.ActiveCheckpoints ??= [];
        document.CastCounts = new Dictionary<string, int>(
            document.CastCounts ?? [], StringComparer.OrdinalIgnoreCase);
        if (loadedSchemaVersion < 2 && document.CastCounts.Count == 0)
        {
            // Starsze pliki nie przechowywały liczby komend. Zachowujemy jako
            // bezpieczne minimum liczbę zarejestrowanych aktywacji buffa.
            foreach (var group in document.Measurements.GroupBy(
                         item => item.BuffName, StringComparer.OrdinalIgnoreCase))
            {
                document.CastCounts[group.Key] = group.Count();
            }
        }
        return document;
    }

    public void Save(BuffHistoryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document.Character);
        document.SchemaVersion = BuffHistoryDocument.CurrentSchemaVersion;
        document.Measurements = document.Measurements
            .GroupBy(item => item.BuffName, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.OrderByDescending(item => item.EndedAtUtc).Take(MaximumMeasurementsPerBuff))
            .OrderByDescending(item => item.EndedAtUtc)
            .ToList();
        DurableJsonFile.Write(GetPath(document.Character), document, SerializerOptions);
    }

    public void Clear(BuffCharacterKey character)
    {
        DeleteIfExists(GetPath(character));
        DeleteIfExists(GetPath(character) + DurableJsonFile.BackupSuffix);
    }

    internal string GetPath(BuffCharacterKey character)
    {
        var readable = Sanitize($"{character.Host}-{character.Port}-{character.CharacterName}");
        var identity = $"{character.Host}\n{character.Port}\n{character.CharacterName}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12].ToLowerInvariant();
        return Path.Combine(_directory, $"{readable}-{hash}.json");
    }

    private static BuffHistoryDocument NewDocument(BuffCharacterKey character) => new() { Character = character };

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
