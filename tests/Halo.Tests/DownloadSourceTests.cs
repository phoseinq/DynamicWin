using Halo.Widgets;

namespace Halo.Tests;

public sealed class DownloadSourceTests
{
    // ── partial-file classification ───────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(@"C:\Users\x\Downloads\ubuntu.iso.crdownload", "ubuntu.iso")]
    [InlineData(@"C:\Users\x\Downloads\movie.mkv.part", "movie.mkv")]
    [InlineData(@"C:\Users\x\Downloads\game.zip.opdownload", "game.zip")]
    [InlineData(@"C:\Users\x\Downloads\setup.exe.partial", "setup.exe")]
    [InlineData(@"C:\Users\x\Downloads\big.bin.aria2", "big.bin")]
    [InlineData(@"C:\Users\x\Downloads\linux.iso.!ut", "linux.iso")]
    public void PartialSuffixes_yield_the_real_filename(string path, string expected)
    {
        Assert.True(PartialFiles.IsPartial(path, out var name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData(@"C:\Users\x\Downloads\ubuntu.iso")]
    [InlineData(@"C:\Users\x\Downloads\notes.txt")]
    [InlineData(@"C:\Users\x\Downloads\archive.partly")]   // suffix must be exact, not a prefix match
    public void OrdinaryFiles_are_not_partial(string path)
    {
        Assert.False(PartialFiles.IsPartial(path, out _));
    }

    [Fact]
    public void ChromesPlaceholderName_is_reported_as_unknown()
    {
        // "Unconfirmed 123456.crdownload" carries no real name, so the caller must fall back to the
        // browser's own record rather than showing the placeholder
        Assert.True(PartialFiles.IsPartial(@"C:\d\Unconfirmed 934617.crdownload", out var name));
        Assert.Equal("", name);
    }

    // ── steam appmanifest ─────────────────────────────────────────────────────────────────────────
    private const string Manifest = """
        "AppState"
        {
        	"appid"		"570"
        	"name"		"Dota 2"
        	"StateFlags"		"1026"
        	"BytesToDownload"		"25108240"
        	"BytesDownloaded"		"12554120"
        }
        """;

    [Fact]
    public void SteamManifest_yields_name_and_bytes()
    {
        Assert.True(SteamInstall.Parse(Manifest, out var item));
        Assert.Equal("Dota 2", item.Name);
        Assert.Equal(12554120, item.Done);
        Assert.Equal(25108240, item.Total);
    }

    [Fact]
    public void SteamManifest_without_byte_counts_is_rejected()
    {
        Assert.False(SteamInstall.Parse("\"AppState\"\n{\n\t\"name\"\t\t\"Half-Life\"\n}", out _));
    }

    [Fact]
    public void SteamManifest_never_reports_more_done_than_total()
    {
        // a finished-then-verifying manifest can briefly carry downloaded > total; a percentage above
        // 100 would be a lie, so Total is clamped up instead
        const string odd = "\"AppState\"\n{\n\t\"name\"\t\t\"X\"\n\t\"BytesToDownload\"\t\t\"100\"\n\t\"BytesDownloaded\"\t\t\"250\"\n}";
        Assert.True(SteamInstall.Parse(odd, out var item));
        Assert.True(item.Done <= item.Total);
    }

    [Fact]
    public void SteamManifest_garbage_does_not_throw()
    {
        Assert.False(SteamInstall.Parse("", out _));
        Assert.False(SteamInstall.Parse("\"\"\"\"\n{{{", out _));
        Assert.False(SteamInstall.Parse("not a manifest at all", out _));
    }

    // ── library folders ───────────────────────────────────────────────────────────────────────────
    [Fact]
    public void LibraryFolders_are_parsed_and_unescaped()
    {
        const string vdf = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Program Files (x86)\\Steam"
            	}
            	"1"
            	{
            		"path"		"H:\\SteamLibrary"
            	}
            }
            """;
        var libs = SteamInstall.ParseLibraries(vdf);
        Assert.Equal(2, libs.Count);
        Assert.Equal(@"C:\Program Files (x86)\Steam", libs[0]);
        Assert.Equal(@"H:\SteamLibrary", libs[1]);
    }

    [Fact]
    public void LibraryFolders_empty_input_is_empty_list()
    {
        Assert.Empty(SteamInstall.ParseLibraries(""));
        Assert.Empty(SteamInstall.ParseLibraries("{}"));
    }
}
