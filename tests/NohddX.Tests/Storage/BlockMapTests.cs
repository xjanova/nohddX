using FluentAssertions;
using NohddX.Storage.CoW;
using Xunit;

namespace NohddX.Tests.Storage;

public class BlockMapTests
{
    [Fact]
    public void New_map_starts_empty()
    {
        using var map = new BlockMap(100);
        map.WrittenBlockCount.Should().Be(0);
        for (long i = 0; i < 100; i++)
            map.IsBlockWritten(i).Should().BeFalse();
    }

    [Fact]
    public void Set_block_makes_it_written()
    {
        using var map = new BlockMap(100);
        map.SetBlockWritten(42);

        map.IsBlockWritten(42).Should().BeTrue();
        map.IsBlockWritten(41).Should().BeFalse();
        map.IsBlockWritten(43).Should().BeFalse();
        map.WrittenBlockCount.Should().Be(1);
    }

    [Fact]
    public void Set_same_block_twice_is_idempotent()
    {
        using var map = new BlockMap(100);
        map.SetBlockWritten(7);
        map.SetBlockWritten(7);
        map.SetBlockWritten(7);
        map.WrittenBlockCount.Should().Be(1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(1000)]
    public async Task Round_trip_via_disk_preserves_state(long totalBlocks)
    {
        var path = Path.Combine(Path.GetTempPath(), $"blockmap-{Guid.NewGuid():N}.bin");
        try
        {
            using (var map = new BlockMap(totalBlocks))
            {
                map.SetBlockWritten(0);
                if (totalBlocks > 1) map.SetBlockWritten(totalBlocks - 1);
                if (totalBlocks > 2) map.SetBlockWritten(totalBlocks / 2);
                await map.SaveAsync(path);
            }

            using var loaded = await BlockMap.LoadAsync(path);
            loaded.TotalBlocks.Should().Be(totalBlocks);
            loaded.IsBlockWritten(0).Should().BeTrue();
            if (totalBlocks > 1) loaded.IsBlockWritten(totalBlocks - 1).Should().BeTrue();
            if (totalBlocks > 2) loaded.IsBlockWritten(totalBlocks / 2).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Out_of_range_throws()
    {
        using var map = new BlockMap(10);
        Action a = () => map.SetBlockWritten(10);
        a.Should().Throw<ArgumentOutOfRangeException>();
        Action b = () => map.SetBlockWritten(-1);
        b.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Clear_resets_all_blocks()
    {
        using var map = new BlockMap(50);
        for (long i = 0; i < 50; i += 3) map.SetBlockWritten(i);
        map.WrittenBlockCount.Should().BeGreaterThan(0);

        map.Clear();
        map.WrittenBlockCount.Should().Be(0);
        for (long i = 0; i < 50; i++)
            map.IsBlockWritten(i).Should().BeFalse();
    }
}
