using System.Net;
using System.Text.RegularExpressions;
using PhotoSweep.Eval.Precision;
using static PhotoSweep.Tests.Grouping.Files;

namespace PhotoSweep.Tests.Evaluation;

public class ReviewPageTests
{
    [Fact]
    public void Page_embeds_the_label_file_so_an_export_parses_back()
    {
        // "&" and "'" in a folder name must survive both the HTML and the embedded JSON.
        var keeper = Photo(@"Tom & Jerry's\keeper.jpg");
        var member = Photo(@"Tom & Jerry's\member.jpg");
        var bucket = new BucketInfo("3–4", 3, 4, 17);
        var labels = new LabelFile
        {
            Folder = @"C:\Photos",
            Seed = 5,
            GroupedAtPHash = 8,
            GroupedAtDHash = 8,
            PairsPerBucket = 30,
            CreatedUtc = DateTime.UnixEpoch,
            Buckets = [bucket],
            Pairs = [new LabelledPair { Id = 1, Bucket = "3–4", KeeperPath = keeper.Path, MemberPath = member.Path, PHash = 4, DHash = 3 }],
        };
        var thumbs = new Dictionary<string, ThumbInfo>
        {
            [keeper.Path] = new("review-thumbs/0.jpg", null),
            [member.Path] = new(null, "online-only, not downloaded"),
        };

        var html = ReviewPage.Render(labels, [(bucket, new CandidatePair(keeper, member, 4, 3))], thumbs);

        var json = Regex.Match(html, "<script type=\"application/json\" id=\"labels\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
        var parsed = LabelFile.FromJson(json);
        Assert.Equal(labels.Pairs, parsed.Pairs);
        Assert.Equal(17, parsed.Buckets.Single().Population);

        Assert.Contains("id=\"pair-1\"", html);
        Assert.Contains("pHash 4 · dHash 3", html);
        Assert.Contains("src=\"review-thumbs/0.jpg\"", html);
        Assert.Contains("online-only, not downloaded", html);
        Assert.Contains(WebUtility.HtmlEncode(@"Tom & Jerry's"), html);
        Assert.Contains("id=\"export\"", html);
    }
}
