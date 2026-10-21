using Filee.Core.Organizing;

namespace Filee.Core.Tests;

public sealed class OrganizePlannerTests
{
    private static readonly DateTime Taken = new(2024, 11, 16, 20, 30, 0);
    private static readonly OrganizeRules Rules = OrganizeRules.Defaults(@"E:\Storage");

    private static PlannedFile Plan(string path) => OrganizePlanner.Plan(path, Rules, _ => Taken);

    [Fact]
    public void Odd_picture_formats_are_converted_to_jpg_and_filed_by_date()
    {
        var plan = Plan(@"C:\Users\me\Downloads\IMG_0001.HEIC");

        Assert.Equal(FileKind.Pictures, plan.Kind);
        Assert.Equal(OrganizeAction.ConvertAndMove, plan.Action);
        Assert.Equal("jpg", plan.TargetFormat);
        Assert.Equal(@"E:\Storage\Pictures\2024\2024-11", plan.DestinationFolder);
    }

    [Fact]
    public void Jpg_and_raw_pictures_are_moved_as_they_are()
    {
        Assert.Equal(OrganizeAction.Move, Plan(@"C:\in\a.jpg").Action);
        var raw = Plan(@"C:\in\DSC_0001.NEF");
        Assert.Equal(OrganizeAction.Move, raw.Action);
        Assert.Null(raw.TargetFormat);
    }

    [Fact]
    public void Videos_convert_to_mp4_and_mp4_moves()
    {
        Assert.Equal("mp4", Plan(@"C:\in\clip.MOV").TargetFormat);
        Assert.Equal(OrganizeAction.Move, Plan(@"C:\in\clip.mp4").Action);
        Assert.Equal(@"E:\Storage\Videos\2024\2024-11", Plan(@"C:\in\clip.mp4").DestinationFolder);
    }

    [Theory]
    [InlineData("report.docx", "Word")]
    [InlineData("budget.xlsx", "Excel")]
    [InlineData("deck.pptx", "PowerPoint")]
    [InlineData("scan.pdf", "PDF")]
    [InlineData("notes.txt", "Text")]
    public void Documents_are_filed_by_type_and_never_converted(string name, string group)
    {
        var plan = Plan(@"C:\in\" + name);

        Assert.Equal(OrganizeAction.Move, plan.Action);
        Assert.Null(plan.TargetFormat);
        Assert.Equal(@"E:\Storage\Documents\" + group, plan.DestinationFolder);
    }

    [Fact]
    public void Unknown_files_and_files_already_in_place_are_left()
    {
        Assert.Equal(OrganizeAction.Leave, Plan(@"C:\in\setup.exe").Action);
        Assert.Equal(OrganizeAction.Leave, Plan(@"E:\Storage\Pictures\2024\2024-11\a.jpg").Action);
    }

    [Fact]
    public void A_file_already_in_place_in_an_odd_format_is_still_converted()
    {
        Assert.Equal(OrganizeAction.ConvertAndMove, Plan(@"E:\Storage\Pictures\2024\2024-11\a.png").Action);
    }

    [Fact]
    public void A_kind_without_a_destination_is_left()
    {
        var rules = OrganizeRules.Defaults(@"E:\Storage");
        rules.Destinations.Remove(FileKind.Audio);

        Assert.Equal(OrganizeAction.Leave, OrganizePlanner.Plan(@"C:\in\song.mp3", rules, _ => Taken).Action);
    }

    [Fact]
    public void Rules_round_trip_through_json_and_keep_case_insensitive_extensions()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("filee-rules-").FullName, "rules.json");
        Rules.Save(path);

        var loaded = OrganizeRules.Load(path);

        Assert.Equal(Rules.Destinations[FileKind.Videos], loaded.Destinations[FileKind.Videos]);
        Assert.Equal("jpg", loaded.ConvertTo["HEIC"]);
    }

    [Fact]
    public void Free_path_adds_a_number_instead_of_overwriting()
    {
        var folder = Directory.CreateTempSubdirectory("filee-free-").FullName;
        File.WriteAllText(Path.Combine(folder, "a.jpg"), "x");
        File.WriteAllText(Path.Combine(folder, "a (2).jpg"), "x");

        Assert.Equal(Path.Combine(folder, "a (3).jpg"), OrganizePlanner.FreePath(folder, "a.jpg"));
    }

    [Fact]
    public void Enumerate_skips_code_and_work_folders()
    {
        var root = Directory.CreateTempSubdirectory("filee-enum-").FullName;
        foreach (var dir in new[] { "photos", "node_modules", ".git", OrganizePlanner.WorkFolderName })
        {
            Directory.CreateDirectory(Path.Combine(root, dir));
            File.WriteAllText(Path.Combine(root, dir, "f.jpg"), "x");
        }

        var files = OrganizePlanner.Enumerate(root, true, Rules).ToList();

        Assert.Equal([Path.Combine(root, "photos", "f.jpg")], files);
    }
}
