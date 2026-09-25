using System.Numerics;

namespace PhotoSweep.Core.Hashing;

public static class Hamming
{
    /// <summary>Number of differing bits (0–64). XOR leaves a 1 wherever the hashes differ; PopCount counts them.</summary>
    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}
