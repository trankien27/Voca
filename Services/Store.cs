using System.IO;
using System.Text.Json;
using Voca.Models;

namespace Voca.Services;

/// <summary>
/// The single owner of the app's data. All windows share one instance and one in-memory
/// <see cref="Data"/>, so there is never a stale copy to overwrite another window's changes.
/// Saving is atomic (temp file + replace, previous version kept as .bak).
/// </summary>
public sealed class Store
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string _backupPath;
    private readonly string _tempPath;

    public AppData Data { get; private set; }
    public string Folder { get; }

    /// <summary>Raised after every save so open windows can refresh.</summary>
    public event Action? Changed;

    /// <param name="folder">Data folder; defaults to %LOCALAPPDATA%\Voca (tests pass their own).</param>
    public Store(string? folder = null)
    {
        Folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voca");
        Directory.CreateDirectory(Folder);
        _path = Path.Combine(Folder, "voca.json");
        _backupPath = _path + ".bak";
        _tempPath = _path + ".tmp";
        Data = Load();
    }

    /// <summary>Applies a change and saves it.</summary>
    public void Update(Action<AppData> change)
    {
        change(Data);
        Save();
    }

    public void Save()
    {
        File.WriteAllText(_tempPath, JsonSerializer.Serialize(Data, JsonOptions));
        if (File.Exists(_path))
            File.Replace(_tempPath, _path, _backupPath, ignoreMetadataErrors: true);
        else
            File.Move(_tempPath, _path);
        Changed?.Invoke();
    }

    private AppData Load()
    {
        if (TryRead(_path, out var data)) return data;

        // Damaged file: keep it for manual recovery and fall back to the last good backup.
        if (File.Exists(_path))
            File.Move(_path, $"{_path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
        if (TryRead(_backupPath, out data))
        {
            Data = data;
            Save();
            return data;
        }
        return new AppData();
    }

    private static bool TryRead(string path, out AppData data)
    {
        data = null!;
        try
        {
            if (!File.Exists(path)) return false;
            var loaded = JsonSerializer.Deserialize<AppData>(File.ReadAllText(path), JsonOptions);
            if (loaded is null) return false;
            data = loaded;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
