using System.Text.Json;

namespace Neuroma.Storage;

public sealed record ReadingPosition(int Chapter, double Fraction, DateTimeOffset UpdatedAt)
{ public static readonly ReadingPosition Start = new(0, 0, DateTimeOffset.MinValue); }

public sealed class ProgressStore
{
    private readonly string _path; private Dictionary<string, ReadingPosition>? _positions;
    public ProgressStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neuroma", "progress.json");
    public ReadingPosition Get(string bookPath)
    { EnsureLoaded(); return _positions!.TryGetValue(Key(bookPath), out ReadingPosition? p) ? p : ReadingPosition.Start; }
    public void Save(string bookPath, ReadingPosition position)
    {
        EnsureLoaded(); _positions![Key(bookPath)] = position;
        string? directory = Path.GetDirectoryName(_path); if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_positions, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, overwrite: true);
    }
    private void EnsureLoaded()
    {
        if (_positions is not null) return;
        try { _positions = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, ReadingPosition>>(File.ReadAllText(_path)) : null; }
        catch (JsonException) { _positions = null; }
        _positions ??= new(StringComparer.OrdinalIgnoreCase);
    }
    private static string Key(string path) => Path.GetFullPath(path).ToUpperInvariant();
}
