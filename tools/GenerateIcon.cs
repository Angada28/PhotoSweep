#:package SixLabors.ImageSharp@3.1.12

// Regenerates the app icon: src/PhotoSweep.Desktop/Assets/PhotoSweep.ico, plus docs/images/icon.png for the README.
//
//   dotnet run tools/GenerateIcon.cs
//
// The design is drawn here from simple shapes (an original design, nothing traced or copied): a blue rounded tile,
// two overlapping photo cards, and a green check badge. Each pixel is supersampled, so edges are anti-aliased at
// every size.
//
// ImageSharp 3.1 renders the pixels and encodes the PNGs, but it has no ICO encoder, so this script writes the ICO
// container itself: a 6-byte header, one 16-byte directory entry per size, then the images. Sizes up to 64 px are
// stored as 32-bit BMPs, which every Windows API reads; 256 px is stored as a PNG, as Windows expects for that size.

using System.Numerics;
using System.Runtime.CompilerServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];

var repoRoot = Path.GetFullPath(Path.Combine(ScriptDirectory(), ".."));
var icoPath = Path.Combine(repoRoot, "src", "PhotoSweep.Desktop", "Assets", "PhotoSweep.ico");
var pngPath = Path.Combine(repoRoot, "docs", "images", "icon.png");

var images = sizes.Select(size => (Size: size, Pixels: Render(size))).ToList();

Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
File.WriteAllBytes(icoPath, WriteIco(images));
Console.WriteLine($"Wrote {icoPath}");

Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
File.WriteAllBytes(pngPath, EncodePng(images[^1].Pixels, 256));
Console.WriteLine($"Wrote {pngPath}");

static string ScriptDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

// ---------------------------------------------------------------------------------------------------------------
// Drawing. Coordinates run 0–1 across the icon, y downwards. Colours are premultiplied RGBA in Vector4.
// ---------------------------------------------------------------------------------------------------------------

static Rgba32[] Render(int size)
{
    var samples = size <= 64 ? 8 : 4; // per axis; small sizes need more to keep thin shapes smooth
    var pixels = new Rgba32[size * size];
    for (var y = 0; y < size; y++)
    {
        for (var x = 0; x < size; x++)
        {
            var sum = Vector4.Zero;
            for (var sy = 0; sy < samples; sy++)
            {
                for (var sx = 0; sx < samples; sx++)
                {
                    var p = new Vector2((x + (sx + 0.5f) / samples) / size, (y + (sy + 0.5f) / samples) / size);
                    sum += Shade(p);
                }
            }

            var c = sum / (samples * samples);
            pixels[y * size + x] = c.W <= 0
                ? new Rgba32(0, 0, 0, 0)
                : new Rgba32(new Vector4(c.X / c.W, c.Y / c.W, c.Z / c.W, c.W)); // un-premultiply
        }
    }

    return pixels;
}

// The colour at one point: shapes painted back to front.
static Vector4 Shade(Vector2 p)
{
    var c = Vector4.Zero;

    // Tile: rounded square with a diagonal gradient.
    if (RoundedRect(p, new(0.5f, 0.5f), new(0.46f, 0.46f), 0.2f) <= 0)
        c = Vector4.Lerp(Colour(0x38BDF8), Colour(0x2563EB), Math.Clamp((p.X + p.Y - 0.1f) / 1.8f, 0, 1));

    // Back photo card: tilted left, half-transparent white.
    var back = Rotate(p - new Vector2(0.41f, 0.39f), -12);
    if (RoundedRect(back, Vector2.Zero, new(0.25f, 0.19f), 0.035f) <= 0)
        c = Over(Colour(0xFFFFFF, 0.55f), c);

    // Front photo card: white frame around a small landscape (sky, sun, two hills).
    var front = Rotate(p - new Vector2(0.53f, 0.55f), 5);
    if (RoundedRect(front, Vector2.Zero, new(0.28f, 0.21f), 0.035f) <= 0)
    {
        c = Colour(0xFFFFFF);
        if (RoundedRect(front, Vector2.Zero, new(0.235f, 0.165f), 0.015f) <= 0)
        {
            c = Colour(0xBAE6FD);
            if (Vector2.Distance(front, new(0.13f, -0.08f)) <= 0.045f)
                c = Colour(0xFBBF24);
            if (front.Y >= -0.04f + Math.Abs(front.X + 0.08f) * 1.1f)
                c = Colour(0x0EA5E9);
            if (front.Y >= 0.02f + Math.Abs(front.X - 0.09f) * 0.9f)
                c = Colour(0x0369A1);
        }
    }

    // Check badge: white ring, green disc, white tick.
    var badge = new Vector2(0.75f, 0.75f);
    if (Vector2.Distance(p, badge) <= 0.185f)
        c = Colour(0xFFFFFF);
    if (Vector2.Distance(p, badge) <= 0.155f)
    {
        c = Colour(0x16A34A);
        var tick = MathF.Min(Segment(p, new(0.675f, 0.755f), new(0.728f, 0.808f)), Segment(p, new(0.728f, 0.808f), new(0.83f, 0.69f)));
        if (tick <= 0.028f)
            c = Colour(0xFFFFFF);
    }

    return c;
}

static Vector4 Colour(int rgb, float alpha = 1) =>
    new Vector4(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1) * alpha;

// Premultiplied "source over destination".
static Vector4 Over(Vector4 src, Vector4 dst) => src + dst * (1 - src.W);

static Vector2 Rotate(Vector2 v, float degrees)
{
    var r = -degrees * MathF.PI / 180; // rotating the point the other way turns the shape by +degrees
    return new(v.X * MathF.Cos(r) - v.Y * MathF.Sin(r), v.X * MathF.Sin(r) + v.Y * MathF.Cos(r));
}

// Signed distance to a rounded rectangle: negative inside, positive outside.
static float RoundedRect(Vector2 p, Vector2 centre, Vector2 half, float radius)
{
    var q = Vector2.Abs(p - centre) - half + new Vector2(radius);
    return Vector2.Max(q, Vector2.Zero).Length() + MathF.Min(MathF.Max(q.X, q.Y), 0) - radius;
}

// Distance from p to the line segment a–b.
static float Segment(Vector2 p, Vector2 a, Vector2 b)
{
    var ab = b - a;
    var t = Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
    return Vector2.Distance(p, a + ab * t);
}

// ---------------------------------------------------------------------------------------------------------------
// Encoding.
// ---------------------------------------------------------------------------------------------------------------

static byte[] EncodePng(Rgba32[] pixels, int size)
{
    using var image = Image.LoadPixelData<Rgba32>(pixels, size, size);
    using var stream = new MemoryStream();
    image.SaveAsPng(stream);
    return stream.ToArray();
}

// ICO = ICONDIR header, one ICONDIRENTRY per image, then the image data. All little-endian.
static byte[] WriteIco(IReadOnlyList<(int Size, Rgba32[] Pixels)> images)
{
    var payloads = images.Select(i => i.Size >= 256 ? EncodePng(i.Pixels, i.Size) : EncodeBmp(i.Pixels, i.Size)).ToList();

    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0);             // reserved
    writer.Write((ushort)1);             // type: 1 = icon
    writer.Write((ushort)images.Count);

    var offset = 6 + 16 * images.Count;
    for (var i = 0; i < images.Count; i++)
    {
        var size = images[i].Size;
        writer.Write((byte)(size >= 256 ? 0 : size)); // width; 0 means 256
        writer.Write((byte)(size >= 256 ? 0 : size)); // height
        writer.Write((byte)0);           // palette colours: none
        writer.Write((byte)0);           // reserved
        writer.Write((ushort)1);         // colour planes
        writer.Write((ushort)32);        // bits per pixel
        writer.Write(payloads[i].Length);
        writer.Write(offset);
        offset += payloads[i].Length;
    }

    foreach (var payload in payloads)
        writer.Write(payload);

    return stream.ToArray();
}

// An ICO "BMP" entry: BITMAPINFOHEADER (height doubled: colour rows + mask rows), BGRA rows bottom-up, then a
// 1-bit AND mask (1 = transparent) that only very old code looks at; the alpha channel is what Windows uses.
static byte[] EncodeBmp(Rgba32[] pixels, int size)
{
    var maskStride = (size + 31) / 32 * 4; // mask rows are padded to 32 bits
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write(40);                    // header size
    writer.Write(size);                  // width
    writer.Write(size * 2);              // height: colour + mask
    writer.Write((ushort)1);             // planes
    writer.Write((ushort)32);            // bits per pixel
    writer.Write(0);                     // BI_RGB (uncompressed)
    writer.Write(size * size * 4 + maskStride * size);
    writer.Write(0); writer.Write(0);    // pixels per metre
    writer.Write(0); writer.Write(0);    // palette colours used / important

    for (var y = size - 1; y >= 0; y--)
    {
        for (var x = 0; x < size; x++)
        {
            var px = pixels[y * size + x];
            writer.Write(px.B); writer.Write(px.G); writer.Write(px.R); writer.Write(px.A);
        }
    }

    for (var y = size - 1; y >= 0; y--)
    {
        var row = new byte[maskStride];
        for (var x = 0; x < size; x++)
        {
            if (pixels[y * size + x].A == 0)
                row[x / 8] |= (byte)(0x80 >> (x % 8));
        }

        writer.Write(row);
    }

    return stream.ToArray();
}
