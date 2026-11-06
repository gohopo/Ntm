using System.Text;
using Ntm.Gateway.Forwarding;

namespace Ntm.Gateway.Tests.Forwarding;

public sealed class InjectedUsageFilterTests
{
    private static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

    [Fact]
    public void ReleasesEverythingWhenNothingIsCut()
    {
        using var filter = new InjectedUsageFilter();
        filter.Append("abcdef"u8);

        Assert.Equal("abc", Text(filter.Release(3)));
        Assert.Equal("def", Text(filter.ReleaseAll()));
    }

    [Fact]
    public void SkipsTheCutRange()
    {
        using var filter = new InjectedUsageFilter();
        filter.Append("keep-DROP-keep"u8);
        filter.Cut(5, 10);

        Assert.Equal("keep-keep", Text(filter.ReleaseAll()));
    }

    [Fact]
    public void WithholdsBytesPastTheReleasePoint()
    {
        using var filter = new InjectedUsageFilter();
        filter.Append("frame1|half"u8);

        Assert.Equal("frame1|", Text(filter.Release(7)));

        filter.Append("frame2|"u8);
        Assert.Equal("halfframe2|", Text(filter.Release(18)));
    }

    [Fact]
    public void CutSpanningTwoAppendsIsRemovedWhole()
    {
        using var filter = new InjectedUsageFilter();
        filter.Append("aaUS"u8);
        Assert.Equal("aa", Text(filter.Release(2)));

        filter.Append("AGEbb"u8);
        filter.Cut(2, 7);

        Assert.Equal("bb", Text(filter.ReleaseAll()));
    }

    [Fact]
    public void ReleaseBeforeACutDoesNotLoseIt()
    {
        using var filter = new InjectedUsageFilter();
        filter.Append("0123456789"u8);
        filter.Cut(6, 8);

        Assert.Equal("0123", Text(filter.Release(4)));
        Assert.Equal("4589", Text(filter.ReleaseAll()));
    }

    [Fact]
    public void ReleaseInsideACutStopsAtTheCut()
    {
        using var filter = new InjectedUsageFilter();
        filter.Append("0123456789"u8);
        filter.Cut(3, 7);

        Assert.Equal("012", Text(filter.Release(5)));
        Assert.Equal("789", Text(filter.ReleaseAll()));
    }

    [Fact]
    public void MultipleCuts()
    {
        using var filter = new InjectedUsageFilter();
        filter.Append("a1b2c3d"u8);
        filter.Cut(1, 2);
        filter.Cut(3, 4);
        filter.Cut(5, 6);

        Assert.Equal("abcd", Text(filter.ReleaseAll()));
    }

    [Fact]
    public void LargeInputGrowsBuffers()
    {
        using var filter = new InjectedUsageFilter();
        string big = new('x', 100_000);
        filter.Append(Encoding.UTF8.GetBytes(big));
        filter.Cut(10, 20);

        Assert.Equal(big.Length - 10, filter.ReleaseAll().Length);
    }
}
