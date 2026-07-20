using Halo.Codex;

namespace Halo.Tests;

public sealed class CodexLimitsTests
{
    [Fact]
    public void MissingLimits_DoNotClobberLastGoodCache()
    {
        using var temp = new TempDirectory();
        var store = new CodexLimitsStore(temp.CachePath);

        store.Update(GoodLimits(22, 41));
        store.Update(Snapshot());

        Assert.Equal(22, store.Current!.Primary!.UsedPercent);
        Assert.Equal(41, store.Current.Secondary!.UsedPercent);
    }

    [Fact]
    public void InvalidLimit_DoesNotReplaceItsLastGoodBucket()
    {
        using var temp = new TempDirectory();
        var store = new CodexLimitsStore(temp.CachePath);

        store.Update(GoodLimits(22, 41));
        store.Update(Snapshot(primary: new CodexLimit(101, 300, DateTimeOffset.UtcNow.AddHours(1))));

        Assert.Equal(22, store.Current!.Primary!.UsedPercent);
        Assert.Equal(41, store.Current.Secondary!.UsedPercent);
    }

    [Fact]
    public void ValidPartialUpdate_PreservesOtherBucketAndReloadsFromDisk()
    {
        using var temp = new TempDirectory();
        var reset = DateTimeOffset.UtcNow.AddDays(7);
        var store = new CodexLimitsStore(temp.CachePath);

        store.Update(GoodLimits(22, 41));
        store.Update(Snapshot(secondary: new CodexLimit(55, 10_080, reset)));

        var reloaded = new CodexLimitsStore(temp.CachePath);
        Assert.Equal(22, reloaded.Current!.Primary!.UsedPercent);
        Assert.Equal(55, reloaded.Current.Secondary!.UsedPercent);
        Assert.Equal(reset, reloaded.Current.Secondary.ResetsAt);
        Assert.NotEqual(DateTimeOffset.MinValue, reloaded.LastSuccess);
    }

    private static CodexSnapshot GoodLimits(double primary, double secondary) => Snapshot(
        primary: new CodexLimit(primary, 300, DateTimeOffset.UtcNow.AddHours(5)),
        secondary: new CodexLimit(secondary, 10_080, DateTimeOffset.UtcNow.AddDays(7)));

    private static CodexSnapshot Snapshot(CodexLimit? primary = null, CodexLimit? secondary = null) => new(
        CodexSurface.Cli, "idle", null, null, null, null, null, 0, 0, 0, 0, 0,
        primary, secondary, DateTimeOffset.UtcNow, false);

    private sealed class TempDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"halo-codex-limits-tests-{Guid.NewGuid():N}");

        internal string CachePath => Path.Combine(_path, "limits.json");

        internal TempDirectory() => Directory.CreateDirectory(_path);

        public void Dispose()
        {
            try { Directory.Delete(_path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
