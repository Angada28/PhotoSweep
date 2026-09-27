using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Imaging;
using PhotoSweep.Core.Scanning;
using static PhotoSweep.Tests.Grouping.Files;

namespace PhotoSweep.Tests.Grouping;

public class GroupKindClassifierTests
{
    private static readonly DateTime Shot = new(2018, 9, 12, 13, 48, 26);

    private static GroupKind Classify(params ScannedFile[] files) => GroupKindClassifier.Classify(files);

    [Fact]
    public void All_byte_identical_is_Copies()
    {
        Assert.Equal(GroupKind.Copies, Classify(Photo("a.jpg", sha: "S"), Photo(@"backup\a.jpg", sha: "S")));
    }

    [Fact]
    public void Copies_wins_even_when_the_names_look_like_a_burst_or_screenshot()
    {
        Assert.Equal(GroupKind.Copies, Classify(Photo("Screenshot_1.png", sha: "S"), Photo("Screenshot_1 (1).png", sha: "S")));
        Assert.Equal(GroupKind.Copies, Classify(Photo("IMG_BURST1.jpg", sha: "S"), Photo("IMG_BURST1 - Copy.jpg", sha: "S")));
    }

    [Fact]
    public void Copies_plus_a_look_alike_is_not_Copies()
    {
        Assert.Equal(GroupKind.LookAlikes, Classify(Photo("a.jpg", sha: "S"), Photo("b.jpg", sha: "S"), Photo("a_small.jpg")));
    }

    [Theory]
    [InlineData("Screenshot_20171106-152712_Instagram")] // real naming patterns from sweep-test
    [InlineData("Screenshot_2016-03-31-16-57-19")]
    [InlineData("Screen_Shot_2017-11-06_at_12.41.31_PM")]
    [InlineData("Screen Shot 2020-01-01 at 10.00.00")]
    [InlineData("Screen_Recording_20200101")]
    [InlineData("Capture d'écran 2020-01-01")]
    public void Screenshot_names_make_a_Screenshots_group(string stem)
    {
        // 4:3 and not a screen size, so only the name can decide.
        Assert.Equal(GroupKind.Screenshots, Classify(Photo(stem + ".png", width: 1000, height: 750), Photo("other.jpg", width: 800, height: 600)));
    }

    [Theory]
    [InlineData(1080, 2340)] // phones, portrait
    [InlineData(1170, 2532)]
    [InlineData(720, 1280)]
    [InlineData(2400, 1080)] // phone, landscape
    [InlineData(1920, 1080)] // desktops
    [InlineData(1366, 768)]
    [InlineData(2560, 1440)]
    public void No_camera_data_at_a_screen_size_is_Screenshots(int width, int height)
    {
        Assert.Equal(GroupKind.Screenshots, Classify(Photo("image1.png", width: width, height: height), Photo("image2.jpg", width: width / 2, height: height / 2)));
    }

    [Theory]
    [InlineData(1600, 1200)] // WhatsApp-style 4:3 re-save without EXIF
    [InlineData(1080, 1440)] // phone width but 4:3, like a camera photo
    [InlineData(2048, 1536)] // iPad screen, but also a common 3 MP camera size
    [InlineData(1000, 1778)] // 16:9 but not a phone width
    public void No_camera_data_at_other_sizes_is_not_Screenshots(int width, int height)
    {
        Assert.Equal(GroupKind.LookAlikes, Classify(Photo("image1.jpg", width: width, height: height), Photo("image2.jpg")));
    }

    [Fact]
    public void Any_member_with_camera_data_rules_out_Screenshots()
    {
        Assert.Equal(GroupKind.LookAlikes, Classify(Photo("Screenshot_1.png", width: 1080, height: 2340), Photo("IMG_1.jpg", camera: "Pixel 3")));
    }

    [Fact]
    public void A_capture_time_alone_does_not_rule_out_Screenshots()
    {
        // Takeout fix-up tools write a DateTimeOriginal into screenshots too; only a camera make or model counts.
        Assert.Equal(GroupKind.Screenshots, Classify(
            Photo("Screenshot_20200601-163605_Tents and Trees.jpg", taken: Shot),
            Photo("Screenshot_20200601-163434_Tents and Trees.jpg", taken: Shot.AddSeconds(-91))));
    }

    [Fact]
    public void Different_capture_times_within_ten_seconds_are_a_Burst()
    {
        Assert.Equal(GroupKind.Burst, Classify(Photo("IMG_1.jpg", taken: Shot), Photo("IMG_2.jpg", taken: Shot.AddSeconds(10))));
    }

    [Fact]
    public void Capture_times_further_apart_or_the_same_are_not_a_Burst()
    {
        Assert.Equal(GroupKind.LookAlikes, Classify(Photo("IMG_1.jpg", taken: Shot), Photo("IMG_2.jpg", taken: Shot.AddSeconds(11))));
        Assert.Equal(GroupKind.LookAlikes, Classify(Photo("IMG_1.jpg", taken: Shot), Photo("IMG_1_small.jpg", taken: Shot)));
        Assert.Equal(GroupKind.LookAlikes, Classify(Photo("IMG_1.jpg", taken: Shot), Photo("IMG_2.jpg"))); // only one time known
    }

    [Theory]
    [InlineData("00001IMG_00001_BURST20180912134826_COVER", "00000IMG_00000_BURST20180912134826")] // Google camera (sweep-test)
    [InlineData("20160101_120000_Burst01", "20160101_120000_Burst02")] // older Samsung
    [InlineData("20160604_162017", "20160604_162017_001")] // Samsung: same stem, numbered suffix (sweep-test)
    [InlineData("20160604_162017_001", "20160604_162017_002")]
    public void Burst_style_names_make_a_Burst(string first, string second)
    {
        Assert.Equal(GroupKind.Burst, Classify(Photo(first + ".jpg"), Photo(second + ".jpg")));
    }

    [Theory]
    [InlineData("IMG_001", "holiday")] // a numbered suffix, but no other member shares its stem
    [InlineData("20160604_162017_001", "20160604_162018_001")] // numbered, different stems
    [InlineData("DSC_0001", "DSC_0002")] // four digits: camera counters, not a sequence suffix
    public void Names_that_only_resemble_a_sequence_are_not_a_Burst(string first, string second)
    {
        Assert.Equal(GroupKind.LookAlikes, Classify(Photo(first + ".jpg"), Photo(second + ".jpg")));
    }

    [Fact]
    public void Screenshots_are_checked_before_burst_names()
    {
        Assert.Equal(GroupKind.Screenshots, Classify(Photo("Screenshot_20200101-120000.png"), Photo("Screenshot_20200101-120000_001.png")));
    }

    [Fact]
    public void Photos_without_details_are_look_alikes()
    {
        var undecoded = new ScannedFile(@"C:\Photos\a.jpg", 1, DefaultTime) { Sha256 = "A" };
        Assert.Equal(GroupKind.LookAlikes, Classify(undecoded, Photo("b.jpg")));
        Assert.False(GroupKindClassifier.IsScreenSize(null));
        Assert.False(GroupKindClassifier.IsScreenSize(new ImageDetails(0, 0)));
    }

    [Fact]
    public void Classifies_a_PhotoGroup_by_its_members()
    {
        var group = Assert.Single(DuplicateGrouper.Group([Photo("a.jpg", sha: "S"), Photo("b.jpg", sha: "S")], MatchLevel.Exact));
        Assert.Equal(GroupKind.Copies, GroupKindClassifier.Classify(group));
    }
}
