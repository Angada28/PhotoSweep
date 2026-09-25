#:package SixLabors.ImageSharp@3.1.12

// Regenerates the committed test photos in tests/PhotoSweep.Tests/TestData/Photos.
//
//   dotnet run tools/GenerateTestImages.cs
//
// Downloads three public-domain / CC0 sample photos from scikit-image (pinned to tag v0.24.0 and checked
// against SHA-256 hashes), shrinks each to 256 px wide and saves it as the "original" JPEG. Then derives the
// variants the fingerprint tests expect to match that original:
//   <name>_half.jpg   resized to 50%
//   <name>_q50.jpg    re-encoded at JPEG quality 50
//   <name>.png        converted to PNG
//   <name>_exif6.jpg  pixels stored rotated 90° anticlockwise with EXIF Orientation = 6 ("rotate 90° CW to
//                     display"), the way a phone saves a portrait shot
// Sources and licences are listed in TestData/Photos/README.md.

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

const string BaseUrl = "https://raw.githubusercontent.com/scikit-image/scikit-image/v0.24.0/skimage/data/";
const int OriginalWidth = 256;

(string Name, string Sha256)[] sources =
[
    ("astronaut", "88431cd9653ccd539741b555fb0a46b61558b301d4110412b5bc28b5e3ea6cb5"),
    ("chelsea", "596aa1e7cb875eb79f437e310381d26b338a81c2da23439704a73c4651e8c4bb"),
    ("coffee", "cc02f8ca188b167c775a7101b5d767d1e71792cf762c33d6fa15a4599b5a8de7"),
];

var outputDir = Path.GetFullPath(Path.Combine(ScriptDirectory(), "..", "tests", "PhotoSweep.Tests", "TestData", "Photos"));
Directory.CreateDirectory(outputDir);

var q90 = new JpegEncoder { Quality = 90 };
var q50 = new JpegEncoder { Quality = 50 };

using var http = new HttpClient();
foreach (var (name, expectedSha) in sources)
{
    var bytes = await http.GetByteArrayAsync($"{BaseUrl}{name}.png");
    var actualSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
    if (actualSha != expectedSha)
        throw new InvalidOperationException($"{name}.png hash mismatch: expected {expectedSha}, got {actualSha}");

    // The original: small, as-if-from-a-camera JPEG.
    using (var source = Image.Load<Rgb24>(bytes))
    {
        source.Mutate(x => x.Resize(OriginalWidth, 0)); // 0 = keep aspect ratio
        source.SaveAsJpeg(Path.Combine(outputDir, $"{name}.jpg"), q90);
    }

    // Variants start from the saved original JPEG, the file a user would actually have.
    using var original = Image.Load<Rgb24>(Path.Combine(outputDir, $"{name}.jpg"));

    using (var half = original.Clone(x => x.Resize(original.Width / 2, 0)))
        half.SaveAsJpeg(Path.Combine(outputDir, $"{name}_half.jpg"), q90);

    original.SaveAsJpeg(Path.Combine(outputDir, $"{name}_q50.jpg"), q50);
    original.SaveAsPng(Path.Combine(outputDir, $"{name}.png"), new PngEncoder());

    using (var rotated = original.Clone(x => x.Rotate(RotateMode.Rotate270)))
    {
        rotated.Metadata.ExifProfile ??= new ExifProfile();
        rotated.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
        rotated.SaveAsJpeg(Path.Combine(outputDir, $"{name}_exif6.jpg"), q90);
    }

    Console.WriteLine($"{name}: done");
}

Console.WriteLine($"Wrote test images to {outputDir}");

static string ScriptDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
