using Halo.ClaudeCode;

namespace Halo.Tests;

public sealed class ClaudeStatusTests
{
    [Fact]
    public void IsLive_IsFalseWhenStatusIsMissing()
    {
        using var temp = new TempStatus();
        var store = NewStore(temp, DateTimeOffset.Parse("2026-07-16T12:00:00Z"), _ => true);

        Assert.False(store.IsLive);
    }

    [Fact]
    public void IsLive_IsFalseWhenPidIsDead()
    {
        using var temp = new TempStatus();
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        WriteStatus(temp.Path, state: "idle", pid: 39156, updatedAt: now);
        var store = NewStore(temp, now, _ => false);

        Assert.False(store.IsLive);
    }

    [Fact]
    public void IsLive_IsTrueWhenPidIsAlive()
    {
        using var temp = new TempStatus();
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        WriteStatus(temp.Path, state: "idle", pid: 39156, updatedAt: now.AddMinutes(-5));
        var store = NewStore(temp, now, pid => pid == 39156);

        Assert.True(store.IsLive);
    }

    [Fact]
    public void IsLive_UsesRecentWorkingStatusWhenPidIsMissing()
    {
        using var temp = new TempStatus();
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        WriteStatus(temp.Path, state: "working", pid: 0, updatedAt: now.AddSeconds(-30));
        var store = NewStore(temp, now, _ => false);

        Assert.True(store.IsLive);
    }

    [Theory]
    [InlineData("waiting_input")]
    [InlineData("compacting")]
    public void IsLive_UsesRecentActiveStateWhenPidIsMissing(string state)
    {
        using var temp = new TempStatus();
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        WriteStatus(temp.Path, state, pid: 0, updatedAt: now.AddSeconds(-29));
        var store = NewStore(temp, now, _ => false);

        Assert.True(store.IsLive);
    }

    [Fact]
    public void IsLive_IsFalseWhenPidlessWorkingStatusIsStale()
    {
        using var temp = new TempStatus();
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        WriteStatus(temp.Path, state: "working", pid: 0, updatedAt: now.AddSeconds(-31));
        var store = NewStore(temp, now, _ => false);

        Assert.False(store.IsLive);
    }

    [Fact]
    public void IsLive_IsFalseForPidlessIdleStatus()
    {
        using var temp = new TempStatus();
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        WriteStatus(temp.Path, state: "idle", pid: 0, updatedAt: now);
        var store = NewStore(temp, now, _ => false);

        Assert.False(store.IsLive);
    }

    [Fact]
    public void IsLive_IsFalseForNegativePid()
    {
        using var temp = new TempStatus();
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        WriteStatus(temp.Path, state: "working", pid: -1, updatedAt: now);
        var store = NewStore(temp, now, _ => true);

        Assert.False(store.IsLive);
    }

    private static StatusStore NewStore(TempStatus temp, DateTimeOffset now, Func<int, bool> processAlive) =>
        new(temp.Path, processAlive, watchFiles: false, clock: () => now);

    private static void WriteStatus(string path, string state, int pid, DateTimeOffset updatedAt) =>
        File.WriteAllText(path, $"{{\"state\":\"{state}\",\"pid\":{pid},\"updatedAt\":\"{updatedAt:O}\"}}");

    private sealed class TempStatus : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"halo-claude-tests-{Guid.NewGuid():N}");

        internal string Path { get; }

        internal TempStatus()
        {
            var directory = System.IO.Path.Combine(_root, "notch");
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "status.json");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
