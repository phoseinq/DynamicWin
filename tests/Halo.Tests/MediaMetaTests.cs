using Halo.Widgets;
using Xunit;

namespace Halo.Tests;

// A release filename is a sentence about the file — "Spy.2015.1080p.BluRay.Farsi.Dubbed.Film2Media" says the
// year, the resolution, the source and who put it out — and the panel was throwing all of it away. These pin
// what may be read out of a name and, more importantly, what may NOT: a guessed publisher presented as fact
// is exactly the sort of invented detail this project has rejected twice.
public class MediaMetaTests
{
    private const string Release = "Spy.2015.1080p.BluRay.Farsi.Dubbed.Film2Media.mkv";

    [Fact]
    public void AReleaseNameGivesUpItsQualitySourceAndPublisher()
    {
        Assert.Equal("1080p", MediaWidget.Quality(Release));
        Assert.Equal("BluRay", MediaWidget.Source(Release));
        Assert.Equal("Film2Media", MediaWidget.Group(Release));
        Assert.Equal("Film2Media  ·  1080p  ·  BluRay", MediaWidget.MetaLine(Release, null, null));
    }

    [Theory]
    [InlineData("Show.S01E02.2160p.WEB-DL.x265", "4K", "WEB-DL")]
    [InlineData("Film.720p.HDTV.x264-GROUP", "720p", "HDTV")]
    [InlineData("Doc.1440p.WEBRip", "1440p", "WEBRip")]
    [InlineData("Movie.2019.UHD.Remux", "4K", "Remux")]
    public void QualityAndSourceComeOffTheName(string name, string q, string src)
    {
        Assert.Equal(q, MediaWidget.Quality(name));
        Assert.Equal(src, MediaWidget.Source(name));
    }

    // the app's own metadata wins: a player that fills in an artist knows better than a filename does
    [Fact]
    public void AnArtistFromThePlayerBeatsAGuessedPublisher()
        => Assert.StartsWith("Melanie De Biasio", MediaWidget.MetaLine(Release, "Melanie De Biasio", null));

    // ...and a resolution the PLAYER reports beats one parsed out of a name, because a name can lie
    [Fact]
    public void AReportedResolutionBeatsTheNamesClaim()
    {
        Assert.Contains("4K", MediaWidget.MetaLine(Release, null, null, resolution: "3840x2160"));
        Assert.DoesNotContain("1080p", MediaWidget.MetaLine(Release, null, null, resolution: "3840x2160"));
    }

    [Theory]
    [InlineData("1920x1080", "1080p")]
    [InlineData("3840x2160", "4K")]
    [InlineData("7680x4320", "8K")]
    [InlineData("640x480", "480p")]
    [InlineData("nonsense", null)]
    [InlineData(null, null)]
    public void AReportedResolutionBecomesTheUnitPeopleUse(string? res, string? label)
        => Assert.Equal(label, MediaWidget.HeightLabel(res));

    // Nothing known must still draw something, or the row collapses and everything under it moves.
    [Fact]
    public void NothingKnownIsASingleDot()
        => Assert.Equal("·", MediaWidget.MetaLine("Some Song", null, null));

    // The refusals. A publisher is only claimed for a name that really is a dotted release; a codec or a
    // quality token at the end is not a publisher, and neither is the last word of a sentence.
    [Theory]
    [InlineData("Spy.mkv")]
    [InlineData("Interstellar (2014).mp4")]
    [InlineData("My holiday video.mp4")]
    [InlineData("Show.S01E01.1080p.x265")]
    [InlineData("Movie.2020.BluRay.1080p")]
    [InlineData(null)]
    public void NoPublisherIsInventedFromAnOrdinaryName(string? name)
        => Assert.Null(MediaWidget.Group(name));

    [Fact]
    public void SizeReadsTheWayPeopleSayIt()
    {
        Assert.Equal("1.4 GB", MediaFileInfo.Human(1_503_238_553));
        Assert.Equal("780 MB", MediaFileInfo.Human(818_089_984));
        Assert.Equal("12 GB", MediaFileInfo.Human(12_884_901_888));
        Assert.Equal("", MediaFileInfo.Human(0));
    }

    // only a video filename is worth looking up: a song title is not a path and must not send the lookup
    // walking the Recent folder for it
    [Theory]
    [InlineData("Spy.2015.mkv", true)]
    [InlineData("clip.MP4", true)]
    [InlineData("Some Song", false)]
    [InlineData("track.mp3", false)]
    public void OnlyAVideoFilenameIsWorthLookingUp(string title, bool yes)
        => Assert.Equal(yes, MediaFileInfo.LooksLikeFile(title));
}

// VLC does not speak SMTC, so its whole timeline comes out of the http status document. It carries more than
// SMTC does: whole-second position and length, and the real stream resolution rather than a filename's claim.
public class VlcStatusTests
{
    private const string Xml = """
        <root><rate>1.5</rate><state>playing</state><time>1287</time><length>7245</length>
        <currentplid>4</currentplid>
        <information><category name='Stream 0'>
        <info name='Video_resolution'>1920x1080</info><info name='Codec'>h264</info>
        </category></information></root>
        """;

    [Fact]
    public void PositionAndLengthComeOutInSeconds()
    {
        var (time, length) = Halo.Widgets.VlcHttp.ParseTime(Xml);
        Assert.Equal(1287, time);
        Assert.Equal(7245, length);
    }

    [Fact]
    public void TheStreamsRealResolutionIsRead()
        => Assert.Equal("1920x1080", Halo.Widgets.VlcHttp.ParseResolution(Xml));

    // a live stream has no length, and a bar scaled by zero would read as 100% or NaN
    [Fact]
    public void AStreamWithNoDurationReportsZeroRatherThanNonsense()
    {
        var (_, length) = Halo.Widgets.VlcHttp.ParseTime("<root><time>12</time><length>0</length></root>");
        Assert.Equal(0, length);
        Assert.Null(Halo.Widgets.VlcHttp.ParseResolution("<root></root>"));
    }
}

// The size lookup matches a title against the shell's Recent shortcuts. Both halves of that broke on the one
// player that prompted it: Windows' Media Player reports the name WITHOUT its extension, so the lookup was
// gated off entirely and the name never matched the shortcut it was sitting right next to.
public class MediaFileMatchTests
{
    private const string Title = "Spy.2015.1080p.BluRay.Farsi.Dubbed.Film2Media";   // as SMTC reports it

    [Fact]
    public void AReleaseNameWithNoExtensionIsStillWorthLookingUp()
    {
        Assert.True(MediaFileInfo.LooksLikeFile(Title));
        Assert.True(MediaFileInfo.LooksLikeFile(Title + ".mkv"));
    }

    [Theory]
    [InlineData("Some Song")]
    [InlineData("track.mp3")]
    [InlineData("a.b")]
    [InlineData(@"C:\Videos\film.mkv")]   // a path is not a title
    public void OrdinaryTitlesAreNot(string title)
        => Assert.False(MediaFileInfo.LooksLikeFile(title));

    // the shortcut points at the file WITH its extension; the title has none
    [Fact]
    public void TheFileIsRecognisedWithOrWithoutTheExtensionTheTitleLacks()
    {
        Assert.True(MediaFileInfo.SameFile(@"D:\Films\" + Title + ".mkv", Title));
        Assert.True(MediaFileInfo.SameFile(@"D:\Films\" + Title, Title));
        Assert.True(MediaFileInfo.SameFile(@"D:\Films\" + Title.ToUpperInvariant() + ".MKV", Title));
    }

    // ...but a different film that merely starts the same way is not the same file
    [Theory]
    [InlineData(@"D:\Films\Spy.2015.1080p.BluRay.Farsi.Dubbed.Film2Media.2.mkv")]
    [InlineData(@"D:\Films\Spy.2015.720p.BluRay.Farsi.Dubbed.Film2Media.mkv")]
    [InlineData(@"D:\Films\Spy.2015.1080p.BluRay.Farsi.Dubbed.Film2Media.txt")]
    public void ANearMissIsNotAMatch(string path)
        => Assert.False(MediaFileInfo.SameFile(path, Title));
}
