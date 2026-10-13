using Jellyfin.Plugin.XRay.Analysis;

namespace Jellyfin.Plugin.XRay.Tests;

public class FolderFilterTests
{
    [Theory]
    [InlineData("/media/movies", "/media/movies/Heat (1995)/Heat.mkv", true)]
    [InlineData("/media/movies/", "/media/movies/Heat.mkv", true)]
    [InlineData("D:\\Media\\Movies", "D:\\Media\\Movies\\Heat\\Heat.mkv", true)]
    [InlineData("D:/Media/Movies", "D:\\Media\\Movies\\Heat.mkv", true)]
    [InlineData("/media/movies", "/media/movies2/Heat.mkv", false)]
    [InlineData("/media/movies", "/media/tv/Show/S01E01.mkv", false)]
    [InlineData("/", "/media/movies/Heat.mkv", true)]
    [InlineData("/media/movies", null, false)]
    public void Contains_MatchesWholeFolderSegments(string folder, string? path, bool expected)
    {
        Assert.Equal(expected, FolderFilter.Contains(folder, path));
    }

    [Fact]
    public void Parse_SplitsLinesAndSkipsBlanks()
    {
        Assert.Equal(["/media/movies", "/media/tv/Show"], FolderFilter.Parse("  /media/movies \r\n\n/media/tv/Show\n   \n"));
        Assert.Empty(FolderFilter.Parse(null));
    }

    [Fact]
    public void Includes_EmptyListMatchesEverything()
    {
        Assert.True(FolderFilter.Includes([], "/anything/file.mkv"));
        Assert.True(FolderFilter.Includes(["/a", "/b"], "/b/file.mkv"));
        Assert.False(FolderFilter.Includes(["/a", "/b"], "/c/file.mkv"));
    }
}
