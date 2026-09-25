using PhotoSweep.Core.Hashing;

namespace PhotoSweep.Tests.Hashing;

public class HammingTests
{
    [Theory]
    [InlineData(0UL, 0UL, 0)]
    [InlineData(0xDEADBEEFUL, 0xDEADBEEFUL, 0)]
    [InlineData(0UL, 1UL, 1)]
    [InlineData(0UL, 0x8000_0000_0000_0000UL, 1)]
    [InlineData(0b1010UL, 0b0101UL, 4)]
    [InlineData(0UL, ulong.MaxValue, 64)]
    public void Distance_counts_differing_bits(ulong a, ulong b, int expected)
    {
        Assert.Equal(expected, Hamming.Distance(a, b));
        Assert.Equal(expected, Hamming.Distance(b, a));
    }
}
