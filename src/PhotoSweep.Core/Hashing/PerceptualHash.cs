namespace PhotoSweep.Core.Hashing;

/// <summary>
/// The hash maths on raw greyscale pixels (row-major, one byte per pixel). No image I/O here, so it can be
/// tested with hand-built arrays; <see cref="Fingerprinter"/> does the loading and shrinking.
/// </summary>
public static class PerceptualHash
{
    public const int PHashSize = 32;          // pHash input is 32×32
    public const int DHashWidth = 9;          // dHash input is 9×8: 8 neighbour comparisons per row
    public const int DHashHeight = 8;
    private const int LowFrequencies = 8;     // keep the 8×8 lowest DCT frequencies → 64 bits

    // cos((2x+1)·u·π / 64) for u in [0,8), x in [0,32). Only the 8 lowest frequencies are needed,
    // so each 1-D pass is 32×8 multiply-adds instead of 32×32.
    private static readonly float[] Cosines = BuildCosineTable();

    /// <summary>
    /// DCT hash: 2-D DCT-II of a 32×32 image, keep the top-left 8×8 coefficients (coarse structure), and set
    /// bit (v·8 + u) when coefficient [v, u] is above the median of those 64.
    /// </summary>
    /// <remarks>
    /// The median includes the DC term [0, 0] (mean brightness), as the widely used Python <c>imagehash</c>
    /// library does. DC is nearly always above the median, so that bit is effectively constant; we accept one
    /// wasted bit to keep distances comparable with that reference.
    /// </remarks>
    public static ulong ComputePHash(ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != PHashSize * PHashSize)
            throw new ArgumentException($"Expected {PHashSize}×{PHashSize} pixels.", nameof(pixels));

        Span<float> rowPass = stackalloc float[PHashSize * LowFrequencies];     // [y, u]
        Span<float> coeffs = stackalloc float[LowFrequencies * LowFrequencies]; // [v, u]

        // The 2-D DCT is separable: transform every row, then every column of the result.
        for (var y = 0; y < PHashSize; y++)
        {
            var row = pixels.Slice(y * PHashSize, PHashSize);
            for (var u = 0; u < LowFrequencies; u++)
            {
                var cos = Cosines.AsSpan(u * PHashSize, PHashSize);
                float acc = 0;
                for (var x = 0; x < PHashSize; x++)
                    acc += row[x] * cos[x];
                rowPass[y * LowFrequencies + u] = acc;
            }
        }

        for (var v = 0; v < LowFrequencies; v++)
        {
            var cos = Cosines.AsSpan(v * PHashSize, PHashSize);
            for (var u = 0; u < LowFrequencies; u++)
            {
                float acc = 0;
                for (var y = 0; y < PHashSize; y++)
                    acc += rowPass[y * LowFrequencies + u] * cos[y];
                coeffs[v * LowFrequencies + u] = acc;
            }
        }

        var median = Median(coeffs);
        ulong hash = 0;
        for (var i = 0; i < coeffs.Length; i++)
        {
            if (coeffs[i] > median)
                hash |= 1UL << i;
        }

        return hash;
    }

    /// <summary>
    /// Difference hash: on a 9×8 image, set bit (y·8 + x) when pixel [y, x + 1] is brighter than its left
    /// neighbour [y, x]. Same convention as <c>imagehash.dhash</c>.
    /// </summary>
    public static ulong ComputeDHash(ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != DHashWidth * DHashHeight)
            throw new ArgumentException($"Expected {DHashWidth}×{DHashHeight} pixels.", nameof(pixels));

        ulong hash = 0;
        for (var y = 0; y < DHashHeight; y++)
        {
            var row = pixels.Slice(y * DHashWidth, DHashWidth);
            for (var x = 0; x < DHashWidth - 1; x++)
            {
                if (row[x + 1] > row[x])
                    hash |= 1UL << (y * (DHashWidth - 1) + x);
            }
        }

        return hash;
    }

    private static float Median(ReadOnlySpan<float> values)
    {
        Span<float> sorted = stackalloc float[values.Length];
        values.CopyTo(sorted);
        sorted.Sort();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }

    private static float[] BuildCosineTable()
    {
        var table = new float[LowFrequencies * PHashSize];
        for (var u = 0; u < LowFrequencies; u++)
        for (var x = 0; x < PHashSize; x++)
            table[u * PHashSize + x] = (float)Math.Cos((2 * x + 1) * u * Math.PI / (2 * PHashSize));
        return table;
    }
}
