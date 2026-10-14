using KoalaDeck.Services;

namespace KoalaDeck.Tests;

public class DeckSyncTests
{
    [Fact]
    public void Sync_line_matches_the_protocol()
    {
        string line = DeckSync.Format([12, 50, 80], [false, false, false], micMuted: true);
        Assert.Equal("SYNC:12,50,80,0,0,0,1\n", line);
    }

    [Fact]
    public void Mute_flags_are_per_channel()
    {
        Assert.Equal("SYNC:0,100,7,1,0,1,0\n", DeckSync.Format([0, 100, 7], [true, false, true], micMuted: false));
    }

    [Theory]
    [InlineData(0.12f, 12)]
    [InlineData(0.125f, 13)]   // Windows rounds half up
    [InlineData(0.1699f, 17)]
    [InlineData(0.004f, 0)]
    [InlineData(1f, 100)]
    [InlineData(1.2f, 100)]
    [InlineData(-0.1f, 0)]
    public void Volume_scalar_becomes_the_percentage_windows_shows(float scalar, int expected)
    {
        Assert.Equal(expected, DeckSync.Percent(scalar));
    }
}
