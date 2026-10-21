// "filee organize": files pictures, videos, documents, audio and archives into the folders the rules give, and
// converts odd picture and video formats to one format on the way (HEIC → JPG, MOV → MP4, ...). A dry run by default;
// --execute does it, one file at a time (gentle on a hard disk), and logs every step to a tab-separated file.
//
// A converted original is deleted only after its result is checked: the picture decodes at the same size (sides may
// swap when the converter applies the photo's rotation), the video has a picture and the same playing time. A moved
// file that crosses drives is copied, compared byte for byte by SHA-256, and only then removed from the source.

using System.Security.Cryptography;
using Filee.Core.Conversion;
using Filee.Core.Organizing;
using Filee.Core.Presets;
using Filee.Engines.Media;
using ImageMagick;

namespace Filee.Cli;

internal sealed class Organizer(OrganizeRules rules, CliHost host, TextWriter output, string logPath)
{
    public int Moved { get; private set; }
    public int Converted { get; private set; }
    public int Left { get; private set; }
    public int Failed { get; private set; }
    public long BytesFreed { get; private set; }

    public static DateTime DateOf(string path)
    {
        if (OrganizePlanner.KindOf(path) == FileKind.Pictures)
        {
            try
            {
                using var image = new MagickImage();
                image.Ping(path);
                var taken = image.GetExifProfile()?.GetValue(ExifTag.DateTimeOriginal)?.Value;
                if (taken is { Length: >= 19 } && DateTime.TryParseExact(taken[..19], "yyyy:MM:dd HH:mm:ss",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
                    return date;
            }
            catch (Exception e) when (e is MagickException or IOException)
            {
                // No readable EXIF: fall back to the file's date.
            }
        }
        return File.GetLastWriteTime(path);
    }

    public async Task RunAsync(IReadOnlyList<PlannedFile> plan, bool execute, CancellationToken cancellationToken)
    {
        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Action == OrganizeAction.Leave)
            {
                Left++;
                continue;
            }
            if (!execute)
            {
                output.WriteLine(item.Action == OrganizeAction.Move
                    ? $"move     {item.Source} -> {item.DestinationFolder}"
                    : $"convert  {item.Source} -> {item.TargetFormat} in {item.DestinationFolder}");
                continue;
            }
            try
            {
                if (item.Action == OrganizeAction.Move)
                {
                    var to = MoveVerified(item.Source, item.DestinationFolder!);
                    Moved++;
                    Log("moved", item.Source, to, "");
                    output.WriteLine($"moved    {item.Source} -> {to}");
                }
                else
                {
                    var to = await ConvertVerifiedAsync(item, cancellationToken);
                    Converted++;
                    Log("converted", item.Source, to, "original deleted after check");
                    output.WriteLine($"converted {item.Source} -> {to}");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Failed++;
                Log("FAILED", item.Source, item.DestinationFolder ?? "", e.Message);
                output.WriteLine($"FAILED   {item.Source}: {e.Message} (original kept)");
            }
        }
    }

    /// <summary>Moves a file into <paramref name="folder"/>: a rename on the same drive, else copy, compare, delete.</summary>
    private string MoveVerified(string source, string folder)
    {
        Directory.CreateDirectory(folder);
        var destination = OrganizePlanner.FreePath(folder, Path.GetFileName(source));
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(source)), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            File.Move(source, destination);
            return destination;
        }
        var partial = destination + ".partial";
        File.Copy(source, partial);
        if (!SameBytes(source, partial))
        {
            File.Delete(partial);
            throw new IOException("the copy does not match the original");
        }
        File.Move(partial, destination);
        File.SetCreationTime(destination, File.GetCreationTime(source));
        File.SetLastWriteTime(destination, File.GetLastWriteTime(source));
        File.Delete(source);
        return destination;
    }

    private async Task<string> ConvertVerifiedAsync(PlannedFile item, CancellationToken cancellationToken)
    {
        var folder = item.DestinationFolder!;
        var work = Path.Combine(folder, OrganizePlanner.WorkFolderName, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var preset = new Preset { TargetFormat = item.TargetFormat! };
            preset = host.Store.Settings.WithDefaultOutput(preset);
            preset.Image.Quality = rules.JpgQuality;
            preset.Output.Location = OutputLocation.CustomFolder;
            preset.Output.CustomFolder = work;
            preset.Output.Conflict = ConflictPolicy.Rename;
            preset.Output.KeepDates = true;

            var job = new ConversionJob([item.Source], preset, item.TargetFormat!);
            using (cancellationToken.Register(job.Cancel))
                await host.Queue.RunAsync(job);
            var file = job.Files[0];
            if (file.State != FileState.Done || file.Outputs.Count != 1)
                throw new InvalidOperationException(file.State == FileState.Done
                    ? $"the conversion made {file.Outputs.Count} files instead of one"
                    : file.ErrorDetail ?? file.ErrorKey ?? "the conversion failed");

            var result = file.Outputs[0];
            await CheckAsync(item, result, cancellationToken);

            var destination = OrganizePlanner.FreePath(folder, Path.GetFileNameWithoutExtension(item.Source) + Path.GetExtension(result));
            File.Move(result, destination);
            File.SetCreationTime(destination, File.GetCreationTime(item.Source));
            File.SetLastWriteTime(destination, File.GetLastWriteTime(item.Source));
            var before = new FileInfo(item.Source).Length;
            File.Delete(item.Source);
            BytesFreed += before - new FileInfo(destination).Length;
            return destination;
        }
        finally
        {
            try
            {
                Directory.Delete(work, true);
                var parent = Path.Combine(folder, OrganizePlanner.WorkFolderName);
                if (!Directory.EnumerateFileSystemEntries(parent).Any())
                    Directory.Delete(parent);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Throws unless the converted file is a whole, readable copy of the original.</summary>
    private static async Task CheckAsync(PlannedFile item, string result, CancellationToken cancellationToken)
    {
        if (new FileInfo(result).Length == 0)
            throw new InvalidDataException("the converted file is empty");
        if (item.Kind == FileKind.Pictures)
        {
            using var converted = new MagickImage(result); // a full decode, not a ping: a cut-off file fails here
            using var original = new MagickImage();
            try
            {
                original.Ping(item.Source);
            }
            catch (MagickException)
            {
                return; // the original's size can't be read here; the decoded result is the check
            }
            var same = (converted.Width == original.Width && converted.Height == original.Height)
                || (converted.Width == original.Height && converted.Height == original.Width);
            if (!same)
                throw new InvalidDataException($"size changed from {original.Width}x{original.Height} to {converted.Width}x{converted.Height}");
            return;
        }
        if (item.Kind == FileKind.Videos)
        {
            var ffprobe = FfmpegConverter.Locate()?.Ffprobe ?? throw new InvalidOperationException("FFmpeg was not found, so the video can't be checked");
            var before = await MediaInfo.ProbeAsync(ffprobe, item.Source, cancellationToken);
            var after = await MediaInfo.ProbeAsync(ffprobe, result, cancellationToken);
            if (before.Video is not null && after.Video is null)
                throw new InvalidDataException("the converted video has no picture");
            if (before.Duration is { } a && after.Duration is { } b)
            {
                var allowed = Math.Max(1.0, a.TotalSeconds * 0.01);
                if (Math.Abs((a - b).TotalSeconds) > allowed)
                    throw new InvalidDataException($"playing time changed from {a:hh\\:mm\\:ss} to {b:hh\\:mm\\:ss}");
            }
            else if (before.Duration is not null)
            {
                throw new InvalidDataException("the converted video has no playing time");
            }
        }
    }

    private static bool SameBytes(string a, string b)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return fa.Length == fb.Length && SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }

    private void Log(string action, string source, string destination, string detail)
    {
        var line = string.Join('\t', DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), action, source, destination, detail.Replace('\t', ' ').Replace('\n', ' '));
        File.AppendAllText(logPath, line + Environment.NewLine);
    }
}
