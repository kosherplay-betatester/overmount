using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkmount.Keyboard.Lamps;

/// <summary>
/// Lighting scenes as shareable files ("*.overmount-scene.json"): one or more scenes with a format tag, so friends can
/// swap their custom presets. Reading is strict about the format and tolerant about the content: a file from a newer
/// version with an effect this one doesn't know is refused as a whole rather than half-loaded.
/// </summary>
public static class SceneFiles
{
    public const string Extension = ".overmount-scene.json";
    public const int MaxFileBytes = 2 * 1024 * 1024, MaxScenes = 200, MaxNameLength = 60, MaxDescriptionLength = 300;
    const int MaxColors = 7, MaxLamps = 512;
    const string Format = "overmount-scene";

    sealed class FileModel
    {
        [JsonPropertyOrder(-2)] public string Format { get; set; } = "";
        [JsonPropertyOrder(-1)] public int Version { get; set; } = 1;
        public List<LightingScene> Scenes { get; set; } = [];
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string Serialize(IEnumerable<LightingScene> scenes) =>
        JsonSerializer.Serialize(new FileModel { Format = Format, Scenes = [.. scenes.Select(s => s.Clone())] }, Json);

    /// <summary>The scenes in <paramref name="json"/>, cleaned up (see <see cref="Sanitize"/>).</summary>
    /// <exception cref="FormatException">Not an OverMount scene file, or it has no scenes.</exception>
    public static IReadOnlyList<LightingScene> Parse(string json)
    {
        FileModel? file;
        try { file = JsonSerializer.Deserialize<FileModel>(json, Json); }
        catch (JsonException e) { throw new FormatException("This isn't an OverMount scene file (or it needs a newer OverMount).", e); }
        if (file is null || file.Format != Format)
            throw new FormatException("This isn't an OverMount scene file.");
        var scenes = (file.Scenes ?? []).Where(s => s is not null).Take(MaxScenes).Select(Sanitize).ToList();
        if (scenes.Count == 0) throw new FormatException("The file has no scenes in it.");
        return scenes;
    }

    /// <summary>
    /// A copy that is safe to edit and draw: a usable name and description, at most <see cref="LightingScene.MaxLayers"/>
    /// layers of up to 7 valid colours, bounded key/LED lists with valid colours only, speed and brightness in range.
    /// </summary>
    public static LightingScene Sanitize(LightingScene scene) => new()
    {
        Name = CleanName(scene.Name) is { Length: > 0 } n ? n : "Imported scene",
        Description = Clip(scene.Description?.Trim() ?? "", MaxDescriptionLength),
        Background = IsHex(scene.Background) ? scene.Background : "000000",
        Layers = [.. (scene.Layers ?? []).Where(l => l is not null).Take(LightingScene.MaxLayers).Select(l => new LightLayer
        {
            Name = CleanName(l.Name) is { Length: > 0 } ln ? ln : "Layer",
            Enabled = l.Enabled,
            Effect = l.Effect,
            ColorMode = l.ColorMode,
            Colors = [.. (l.Colors ?? []).Where(IsHex).Take(MaxColors)],
            Direction = l.Direction,
            Speed = Math.Clamp(l.Speed, 1, 10),
            Brightness = Math.Clamp(l.Brightness, 0, 100),
            AllKeys = l.AllKeys,
            Keys = [.. (l.Keys ?? []).Distinct().Take(MaxLamps)],
            AllEdges = l.AllEdges,
            EdgeLamps = [.. (l.EdgeLamps ?? []).Distinct().Take(MaxLamps)],
            KeyColors = (l.KeyColors ?? []).Where(kv => IsHex(kv.Value)).Take(MaxLamps).ToDictionary(),
            EdgeColors = (l.EdgeColors ?? []).Where(kv => IsHex(kv.Value)).Take(MaxLamps).ToDictionary(),
        })],
    };

    /// <summary>"RRGGBB", optionally with a leading '#'.</summary>
    public static bool IsHex(string? s)
    {
        if (s is null) return false;
        var hex = s.StartsWith('#') ? s.AsSpan(1) : s.AsSpan();
        if (hex.Length != 6) return false;
        foreach (char c in hex) if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }

    /// <summary>Trimmed, single-line and at most <see cref="MaxNameLength"/> characters.</summary>
    public static string CleanName(string? name) =>
        Clip(new string((name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim(), MaxNameLength);

    /// <summary><paramref name="name"/>, or "name (2)", "name (3)" … if it is taken (case-insensitive).</summary>
    public static string UniqueName(string name, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name)) return name;
        for (int i = 2; ; i++)
        {
            var suffix = $" ({i})";
            var candidate = Clip(name, MaxNameLength - suffix.Length) + suffix;
            if (!used.Contains(candidate)) return candidate;
        }
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd();
}
