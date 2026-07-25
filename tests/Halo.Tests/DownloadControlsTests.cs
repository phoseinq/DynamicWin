using Halo.Widgets;
using Xunit;
using Ctl = Halo.Widgets.DownloadWidget.DlCtl;

namespace Halo.Tests;

// The panel used to decide its button row twice — once in the painter, once in the hit-tester — and they
// drifted: browser downloads were drawn with three chips while only two hit rects existed, so Cancel did
// nothing and the other two were offset by the re-centring. Both now read DownloadWidget.Row, and these
// pin the row so a fourth chip can't be added to one side only.
public class DownloadControlsTests
{
    [Fact]
    public void BrowserDownload_offers_cancel()
    {
        var row = DownloadWidget.Row(named: true, store: false, canControl: false, hasWindow: false, hasPath: true);
        Assert.Equal(new[] { Ctl.ShowInFolder, Ctl.RevealOwner, Ctl.Cancel }, row);
    }

    [Fact]
    public void StoreDownload_offers_pause_and_cancel()
    {
        var row = DownloadWidget.Row(named: true, store: true, canControl: true, hasWindow: false, hasPath: false);
        Assert.Equal(new[] { Ctl.PauseResume, Ctl.StoreCancel }, row);
    }

    // an uncontrollable Store item (a game staging through GDK) has no path either — nothing to offer
    [Fact]
    public void StoreDownload_without_control_offers_nothing()
    {
        var row = DownloadWidget.Row(named: true, store: true, canControl: false, hasWindow: false, hasPath: false);
        Assert.Empty(row);
    }

    // a window-scanned manager wins over the partial file it is writing: quitting it is a real stop,
    // deleting one of its part files just corrupts the job
    [Fact]
    public void WindowedDownloader_prefers_quitting_the_app_over_deleting_the_file()
    {
        var row = DownloadWidget.Row(named: true, store: false, canControl: false, hasWindow: true, hasPath: true);
        Assert.Equal(new[] { Ctl.Reveal, Ctl.Stop }, row);
    }

    [Fact]
    public void NoDownload_offers_nothing()
    {
        Assert.Empty(DownloadWidget.Row(named: false, store: false, canControl: false, hasWindow: true, hasPath: true));
    }
}
