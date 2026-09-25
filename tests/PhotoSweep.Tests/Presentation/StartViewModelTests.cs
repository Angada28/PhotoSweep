using PhotoSweep.Core.Grouping;
using PhotoSweep.Presentation;
using PhotoSweep.Tests.Scanning;

namespace PhotoSweep.Tests.Presentation;

public class StartViewModelTests
{
    private readonly FakeFolderPicker _picker = new();
    private readonly List<ScanRequest> _scans = [];

    private StartViewModel Create(FakeKnownFolders? known = null) => new(_picker, known ?? new FakeKnownFolders(), _scans.Add);

    private StartViewModel WithFolders(params string[] folders)
    {
        _picker.WillPick(folders);
        var vm = Create();
        vm.AddFolderCommand.Execute(null);
        return vm;
    }

    [Fact]
    public void Starts_with_no_folders_and_Same_photo_selected()
    {
        var vm = Create();

        Assert.Empty(vm.Folders);
        Assert.Equal(MatchLevel.SamePhoto, vm.SelectedStrictness.Level);
        Assert.Equal([MatchLevel.Exact, MatchLevel.SamePhoto, MatchLevel.Similar], vm.Strictness.Select(s => s.Level));
    }

    [Fact]
    public void Add_folder_adds_every_picked_folder_normalized()
    {
        var vm = WithFolders(@"C:\Photos\", @"D:\Camera");

        Assert.Equal([@"C:\Photos", @"D:\Camera"], vm.Folders);
        Assert.Equal(1, _picker.Calls);
    }

    [Fact]
    public void Cancelling_the_picker_adds_nothing()
    {
        var vm = Create();

        vm.AddFolderCommand.Execute(null);

        Assert.Empty(vm.Folders);
    }

    [Fact]
    public void Duplicates_are_ignored_whatever_their_case_or_trailing_separator()
    {
        var vm = WithFolders(@"C:\Photos", @"c:\photos\", @"C:\Photos");

        Assert.Equal([@"C:\Photos"], vm.Folders);
    }

    [Fact]
    public void Folder_inside_a_chosen_folder_is_ignored()
    {
        var vm = WithFolders(@"C:\Photos", @"C:\Photos\2024");

        Assert.Equal([@"C:\Photos"], vm.Folders);
    }

    [Fact]
    public void Adding_an_outer_folder_replaces_the_chosen_folders_inside_it()
    {
        var vm = WithFolders(@"C:\Photos\2023", @"D:\Camera", @"C:\Photos\2024");

        _picker.WillPick(@"C:\Photos");
        vm.AddFolderCommand.Execute(null);

        Assert.Equal([@"D:\Camera", @"C:\Photos"], vm.Folders);
    }

    [Fact]
    public void Sibling_with_a_common_prefix_is_not_treated_as_nested()
    {
        var vm = WithFolders(@"C:\Photos", @"C:\Photos2");

        Assert.Equal([@"C:\Photos", @"C:\Photos2"], vm.Folders);
    }

    [Fact]
    public void Remove_folder_removes_only_that_folder()
    {
        var vm = WithFolders(@"C:\Photos", @"D:\Camera");

        vm.RemoveFolderCommand.Execute(@"C:\Photos");

        Assert.Equal([@"D:\Camera"], vm.Folders);
    }

    [Fact]
    public void Quick_buttons_add_the_known_folders()
    {
        var vm = Create(new FakeKnownFolders(pictures: @"C:\Users\Me\Pictures", oneDrive: @"C:\Users\Me\OneDrive"));

        vm.AddPicturesCommand.Execute(null);
        vm.AddOneDriveCommand.Execute(null);

        Assert.True(vm.HasOneDrive);
        Assert.Equal([@"C:\Users\Me\Pictures", @"C:\Users\Me\OneDrive"], vm.Folders);
    }

    [Fact]
    public void Quick_buttons_are_unavailable_when_the_known_folder_is_missing()
    {
        var vm = Create(new FakeKnownFolders(pictures: null, oneDrive: null));

        Assert.False(vm.HasOneDrive);
        Assert.False(vm.AddOneDriveCommand.CanExecute(null));
        Assert.False(vm.AddPicturesCommand.CanExecute(null));
    }

    [Fact]
    public void Drop_adds_existing_folders_and_ignores_files_and_missing_paths()
    {
        using var temp = new TempPhotoFolder();
        var file = temp.AddBytes("photo.jpg", [1, 2, 3]);
        var missing = Path.Combine(temp.Root, "not-there");
        var vm = Create();

        vm.DropFoldersCommand.Execute(new[] { temp.Root, file, missing, temp.Outside });

        Assert.Equal([temp.Root, temp.Outside], vm.Folders);
    }

    [Fact]
    public void Drop_applies_the_nesting_rule_too()
    {
        using var temp = new TempPhotoFolder();
        var inner = Path.Combine(temp.Root, "inner");
        Directory.CreateDirectory(inner);
        var vm = Create();

        vm.DropFoldersCommand.Execute(new[] { inner, temp.Root });

        Assert.Equal([temp.Root], vm.Folders);
    }

    [Fact]
    public void Scan_is_enabled_only_while_at_least_one_folder_is_chosen()
    {
        _picker.WillPick(@"C:\Photos");
        var vm = Create();
        var changes = 0;
        vm.ScanCommand.CanExecuteChanged += (_, _) => changes++;

        Assert.False(vm.ScanCommand.CanExecute(null));

        vm.AddFolderCommand.Execute(null);
        Assert.True(vm.ScanCommand.CanExecute(null));

        vm.RemoveFolderCommand.Execute(@"C:\Photos");
        Assert.False(vm.ScanCommand.CanExecute(null));

        Assert.True(changes >= 2, "the Scan button is only re-evaluated when CanExecuteChanged fires");
    }

    [Theory]
    [InlineData(MatchLevel.Exact)]
    [InlineData(MatchLevel.SamePhoto)]
    [InlineData(MatchLevel.Similar)]
    public void Scan_builds_a_request_from_the_folders_and_chosen_strictness(MatchLevel level)
    {
        var vm = WithFolders(@"C:\Photos", @"D:\Camera");
        vm.SelectedStrictness = vm.Strictness.Single(s => s.Level == level);

        vm.ScanCommand.Execute(null);

        var request = Assert.Single(_scans);
        Assert.Equal(level, request.Level);
        Assert.Equal([@"C:\Photos", @"D:\Camera"], request.Options.Folders);
        Assert.True(request.Options.Recursive);
        Assert.False(request.Options.IncludeOnlineOnlyFiles); // downloading cloud files is never the default (CLAUDE.md rule 6)
    }

    [Fact]
    public void Scan_request_is_a_snapshot_of_the_folder_list()
    {
        var vm = WithFolders(@"C:\Photos");
        vm.ScanCommand.Execute(null);

        vm.RemoveFolderCommand.Execute(@"C:\Photos");

        Assert.Equal([@"C:\Photos"], _scans.Single().Options.Folders);
    }
}
