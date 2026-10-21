// End-to-end tests of "filee organize": real engines, files in a temporary inbox, a temporary storage root.

namespace Filee.Cli.Tests;

public sealed class OrganizeTests : IDisposable
{
    private readonly string _inbox = Directory.CreateTempSubdirectory("filee-org-in-").FullName;
    private readonly string _root = Directory.CreateTempSubdirectory("filee-org-root-").FullName;
    private readonly string _settings = Directory.CreateTempSubdirectory("filee-org-settings-").FullName;
    private string LogPath => Path.Combine(_settings, "log.tsv");

    public void Dispose()
    {
        foreach (var dir in new[] { _inbox, _root, _settings })
            try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private string Picture(string name, int width = 40, int height = 30)
    {
        var path = Path.Combine(_inbox, name);
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Orange, (uint)width, (uint)height);
        image.Write(path);
        File.SetLastWriteTime(path, new DateTime(2023, 5, 7, 10, 0, 0));
        return path;
    }

    private async Task<(int Code, string Output, string Errors)> Run(params string[] args)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var code = await Cli.RunAsync(args, output, errors, TestContext.Current.CancellationToken, () => CliHost.Create(_settings));
        return (code, output.ToString(), errors.ToString());
    }

    [Fact]
    public async Task A_dry_run_changes_nothing()
    {
        var png = Picture("screen.png");

        var (code, output, _) = await Run("organize", _inbox, "--root", _root, "--log", LogPath);

        Assert.Equal(Cli.Success, code);
        Assert.Contains("convert  ", output);
        Assert.Contains("Nothing was changed", output);
        Assert.True(File.Exists(png));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public async Task Converts_odd_pictures_to_jpg_files_them_by_date_and_deletes_the_original_after_the_check()
    {
        var png = Picture("screen.png", 64, 48);

        var (code, output, errors) = await Run("organize", _inbox, "--root", _root, "--execute", "--log", LogPath);

        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        var filed = Path.Combine(_root, "Pictures", "2023", "2023-05", "screen.jpg");
        Assert.True(File.Exists(filed), output);
        Assert.False(File.Exists(png));
        using var result = new ImageMagick.MagickImage(filed);
        Assert.Equal((64u, 48u), (result.Width, result.Height));
        Assert.Equal(new DateTime(2023, 5, 7, 10, 0, 0), File.GetLastWriteTime(filed));
        Assert.False(Directory.Exists(Path.Combine(_root, "Pictures", "2023", "2023-05", Core.Organizing.OrganizePlanner.WorkFolderName)));
        Assert.Contains("\tconverted\t", File.ReadAllText(LogPath));
    }

    [Fact]
    public async Task Moves_documents_and_jpgs_as_they_are_and_leaves_unknown_files()
    {
        var jpg = Picture("photo.jpg");
        var doc = Path.Combine(_inbox, "report.docx");
        File.WriteAllBytes(doc, [1, 2, 3, 4]);
        var exe = Path.Combine(_inbox, "setup.exe");
        File.WriteAllBytes(exe, [9]);
        var jpgBytes = File.ReadAllBytes(jpg);

        var (code, output, _) = await Run("organize", _inbox, "--root", _root, "--execute", "--log", LogPath);

        Assert.Equal(Cli.Success, code);
        Assert.Equal(jpgBytes, File.ReadAllBytes(Path.Combine(_root, "Pictures", "2023", "2023-05", "photo.jpg")));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(_root, "Documents", "Word", "report.docx")));
        Assert.True(File.Exists(exe));
        Assert.Contains("left 1", output);
    }

    [Fact]
    public async Task A_file_that_fails_to_convert_keeps_its_original()
    {
        var broken = Path.Combine(_inbox, "broken.png");
        File.WriteAllBytes(broken, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);

        var (code, output, _) = await Run("organize", _inbox, "--root", _root, "--execute", "--log", LogPath);

        Assert.Equal(Cli.SomeFailed, code);
        Assert.True(File.Exists(broken));
        Assert.Contains("FAILED", output);
        Assert.Contains("original kept", output);
        Assert.Contains("\tFAILED\t", File.ReadAllText(LogPath));
    }

    [Fact]
    public async Task A_name_already_taken_gets_a_number_instead_of_overwriting()
    {
        var target = Path.Combine(_root, "Pictures", "2023", "2023-05");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "photo.jpg"), [7]);
        Picture("photo.jpg");

        var (code, _, _) = await Run("organize", _inbox, "--root", _root, "--execute", "--log", LogPath);

        Assert.Equal(Cli.Success, code);
        Assert.Equal([7], File.ReadAllBytes(Path.Combine(target, "photo.jpg")));
        Assert.True(File.Exists(Path.Combine(target, "photo (2).jpg")));
    }

    [Fact]
    public async Task Needs_a_root_or_rules()
    {
        var (code, _, errors) = await Run("organize", _inbox);

        Assert.Equal(Cli.UsageError, code);
        Assert.Contains("--root", errors);
    }
}
