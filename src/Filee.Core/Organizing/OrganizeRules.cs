// Rules for "filee organize": where each kind of file is filed, and which formats are converted on the way so a
// folder ends up in one format per kind (photos JPG, videos MP4). Loaded from a JSON file; the defaults file
// everything under one root folder.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Filee.Core.Organizing;

/// <summary>The kind of file, which decides its destination.</summary>
public enum FileKind
{
    Other,
    Pictures,
    Videos,
    Documents,
    Audio,
    Archives,
}

public sealed class OrganizeRules
{
    /// <summary>
    /// Destination folder per kind. Tokens: <c>{yyyy}</c>, <c>{MM}</c>, <c>{yyyy-MM}</c> (the file's date: when the
    /// photo was taken, else when the file was last changed), <c>{ext}</c> (lower case, no dot) and <c>{group}</c>
    /// (documents: PDF, Word, Excel, PowerPoint, Text or Other). A kind without a destination is left where it is.
    /// </summary>
    public Dictionary<FileKind, string> Destinations { get; set; } = [];

    /// <summary>Extension (no dot) → format to convert to before filing, e.g. heic → jpg.</summary>
    public Dictionary<string, string> ConvertTo { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>JPG quality for converted pictures (1-100).</summary>
    public int JpgQuality { get; set; } = 92;

    /// <summary>Folder names that are never entered (code, app data, version control).</summary>
    public List<string> SkipFolders { get; set; } =
        [".git", "node_modules", "vendor", "target", "bin", "obj", "AppData", "$RECYCLE.BIN", "System Volume Information"];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Everything filed under <paramref name="root"/>; odd picture and video formats converted to JPG and MP4.</summary>
    public static OrganizeRules Defaults(string root) => new()
    {
        Destinations = new()
        {
            [FileKind.Pictures] = Path.Combine(root, "Pictures", "{yyyy}", "{yyyy-MM}"),
            [FileKind.Videos] = Path.Combine(root, "Videos", "{yyyy}", "{yyyy-MM}"),
            [FileKind.Documents] = Path.Combine(root, "Documents", "{group}"),
            [FileKind.Audio] = Path.Combine(root, "Audio", "{yyyy}"),
            [FileKind.Archives] = Path.Combine(root, "Archives"),
        },
        ConvertTo = new(StringComparer.OrdinalIgnoreCase)
        {
            ["heic"] = "jpg", ["heif"] = "jpg", ["png"] = "jpg", ["webp"] = "jpg", ["tif"] = "jpg", ["tiff"] = "jpg", ["bmp"] = "jpg",
            ["mov"] = "mp4", ["avi"] = "mp4", ["3gp"] = "mp4", ["mts"] = "mp4", ["m2ts"] = "mp4", ["wmv"] = "mp4",
            ["mpg"] = "mp4", ["mpeg"] = "mp4", ["mkv"] = "mp4",
        },
    };

    public static OrganizeRules Load(string path)
    {
        var rules = JsonSerializer.Deserialize<OrganizeRules>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("The rules file is empty.");
        // JSON does not keep the case-insensitive comparer.
        rules.ConvertTo = new Dictionary<string, string>(rules.ConvertTo, StringComparer.OrdinalIgnoreCase);
        if (rules.JpgQuality is < 1 or > 100)
            throw new InvalidDataException("JpgQuality must be from 1 to 100.");
        return rules;
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
}
