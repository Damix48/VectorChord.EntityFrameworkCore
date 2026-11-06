using VectorChord.EntityFrameworkCore;
using Xunit;

namespace VectorChord.Tests;

public sealed class TuningTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(99_999, 0)]
    [InlineData(100_000, 200)]
    [InlineData(1_000_000, 2000)]
    [InlineData(2_000_000, 5657)]      // 4√N
    [InlineData(50_000_000, 56569)]    // 8√N
    [InlineData(100_000_000, 80000)]
    public void Recommended_lists_follows_the_documented_bands(long rows, int lists)
        => Assert.Equal(lists, VectorChordRqTuning.RecommendedLists(rows));

    [Fact]
    public void Recommended_lists_rejects_negative_rows()
        => Assert.Throws<ArgumentOutOfRangeException>(() => VectorChordRqTuning.RecommendedLists(-1));
}
