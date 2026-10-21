// Decides, without touching anything, what "filee organize" does with each file: file it as it is, convert it and
// file the result, or leave it. The command shows this plan on a dry run and carries it out with --execute.

namespace Filee.Core.Organizing;

public enum OrganizeAction
{
    /// <summary>Not filed: unknown kind, no destination for its kind, or already in its place.</summary>
    Leave,

    /// <summary>Moved to its destination as it is.</summary>
    Move,

    /// <summary>Converted to <see cref="PlannedFile.TargetFormat"/>; the result is filed and the original deleted.</summary>
    ConvertAndMove,
}

public sealed record PlannedFile(string Source, FileKind Kind, OrganizeAction Action, string? DestinationFolder,
    string? TargetFormat, string Reason);

public static class OrganizePlanner
{
    private static readonly Dictionary<string, FileKind> Kinds = Build(
        (FileKind.Pictures, "jpg jpeg png heic heif webp tif tiff gif bmp nef cr2 cr3 arw dng raf orf rw2"),
        (FileKind.Videos, "mp4 mov m4v avi mkv mts m2ts 3gp wmv mpg mpeg webm"),
        (FileKind.Documents, "pdf doc docx xls xlsx csv ppt pptx txt rtf odt ods odp pages numbers key md"),
        (FileKind.Audio, "mp3 m4a wav flac aac ogg wma"),
        (FileKind.Archives, "zip rar 7z tar gz"));

    public static FileKind KindOf(string path) =>
        Kinds.TryGetValue(Extension(path), out var kind) ? kind : FileKind.Other;

    /// <summary>The documents subfolder for <c>{group}</c>.</summary>
    public static string DocumentGroup(string path) => Extension(path) switch
    {
        "pdf" => "PDF",
        "doc" or "docx" or "odt" or "rtf" or "pages" => "Word",
        "xls" or "xlsx" or "csv" or "ods" or "numbers" => "Excel",
        "ppt" or "pptx" or "odp" or "key" => "PowerPoint",
        "txt" or "md" => "Text",
        _ => "Other",
    };

    /// <param name="dateOf">The file's date for the folder tokens (when the photo was taken, else last changed).</param>
    public static PlannedFile Plan(string source, OrganizeRules rules, Func<string, DateTime> dateOf)
    {
        var kind = KindOf(source);
        if (kind == FileKind.Other)
            return new(source, kind, OrganizeAction.Leave, null, null, "not a picture, video, document, audio file or archive");
        if (!rules.Destinations.TryGetValue(kind, out var template) || string.IsNullOrWhiteSpace(template))
            return new(source, kind, OrganizeAction.Leave, null, null, $"no destination set for {kind}");

        var folder = Path.GetFullPath(Fill(template, source, dateOf));
        var target = rules.ConvertTo.TryGetValue(Extension(source), out var to) && !string.Equals(to, Extension(source), StringComparison.OrdinalIgnoreCase)
            ? to.ToLowerInvariant()
            : null;
        if (target is null && SameFolder(Path.GetDirectoryName(Path.GetFullPath(source))!, folder))
            return new(source, kind, OrganizeAction.Leave, folder, null, "already in its place");
        return target is null
            ? new(source, kind, OrganizeAction.Move, folder, null, "")
            : new(source, kind, OrganizeAction.ConvertAndMove, folder, target, "");
    }

    /// <summary>Files under the folders, skipping the rule's skip folders, in a stable order.</summary>
    public static IEnumerable<string> Enumerate(string folder, bool recursive, OrganizeRules rules)
    {
        var skip = new HashSet<string>(rules.SkipFolders, StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>([folder]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(current);
                dirs = recursive ? Directory.GetDirectories(current) : [];
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                continue;
            }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
                yield return file;
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            for (var i = dirs.Length - 1; i >= 0; i--)
            {
                var name = Path.GetFileName(dirs[i]);
                if (!skip.Contains(name) && !name.StartsWith(WorkFolderName, StringComparison.OrdinalIgnoreCase))
                    pending.Push(dirs[i]);
            }
        }
    }

    /// <summary>Hidden work folder the converter writes into before a result is checked and filed.</summary>
    public const string WorkFolderName = ".filee-organize-work";

    /// <summary>A free name in <paramref name="folder"/>: "name.ext", else "name (2).ext", "name (3).ext", ...</summary>
    public static string FreePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{stem} ({n}){ext}");
        return path;
    }

    private static string Fill(string template, string source, Func<string, DateTime> dateOf)
    {
        var needsDate = template.Contains("{yyyy", StringComparison.Ordinal) || template.Contains("{MM}", StringComparison.Ordinal);
        var date = needsDate ? dateOf(source) : default;
        return template
            .Replace("{yyyy-MM}", date.ToString("yyyy-MM"), StringComparison.Ordinal)
            .Replace("{yyyy}", date.ToString("yyyy"), StringComparison.Ordinal)
            .Replace("{MM}", date.ToString("MM"), StringComparison.Ordinal)
            .Replace("{ext}", Extension(source), StringComparison.Ordinal)
            .Replace("{group}", DocumentGroup(source), StringComparison.Ordinal);
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static string Extension(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    private static Dictionary<string, FileKind> Build(params (FileKind Kind, string Extensions)[] groups)
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, extensions) in groups)
            foreach (var ext in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                map[ext] = kind;
        return map;
    }
}
