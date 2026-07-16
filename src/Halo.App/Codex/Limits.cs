using System;
using System.IO;
using System.Text.Json;

namespace Halo.Codex;

internal sealed record CodexCachedLimits(CodexLimit? Primary, CodexLimit? Secondary);

// Rate-limit values are sourced only from Codex rollout events. The cache keeps the last valid
// bucket from each event so incomplete or malformed rollout data never blanks the widget.
internal sealed class CodexLimitsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _cachePath;
    private readonly Func<DateTimeOffset> _clock;

    internal CodexCachedLimits? Current { get; private set; }
    internal DateTimeOffset LastSuccess { get; private set; }
    internal int Version { get; private set; }

    internal CodexLimitsStore(string cachePath, Func<DateTimeOffset>? clock = null)
    {
        _cachePath = cachePath;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Load();
    }

    internal void Update(CodexSnapshot snapshot)
    {
        var primary = Valid(snapshot.PrimaryLimit) ? snapshot.PrimaryLimit : Current?.Primary;
        var secondary = Valid(snapshot.SecondaryLimit) ? snapshot.SecondaryLimit : Current?.Secondary;
        if (primary is null && secondary is null)
            return;

        var next = new CodexCachedLimits(primary, secondary);
        if (!Equals(Current, next))
        {
            Current = next;
            Version++;
        }

        LastSuccess = _clock();
        Save();
    }

    private void Load()
    {
        try
        {
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cachePath), JsonOptions);
            var primary = Valid(cache?.Primary) ? cache!.Primary : null;
            var secondary = Valid(cache?.Secondary) ? cache!.Secondary : null;
            if (primary is null && secondary is null)
                return;

            Current = new CodexCachedLimits(primary, secondary);
            LastSuccess = cache?.SavedAt ?? DateTimeOffset.MinValue;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (JsonException)
        {
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_cachePath);
        if (string.IsNullOrEmpty(directory))
            return;

        var temporaryPath = _cachePath + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            var cache = new CacheFile(Current?.Primary, Current?.Secondary, LastSuccess);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(cache, JsonOptions));
            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool Valid(CodexLimit? limit) =>
        limit is not null && limit.UsedPercent >= 0 && limit.UsedPercent <= 100;

    private sealed record CacheFile(CodexLimit? Primary, CodexLimit? Secondary, DateTimeOffset? SavedAt);
}

// Widget-facing facade: a manual refresh rescans local rollout data only. It never calls a usage API.
internal static class CodexLimits
{
    private static readonly CodexLimitsStore Cache = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "codex-limits-cache.json"));
    private static CodexStatusStore? _statusStore;

    internal static CodexCachedLimits? Current => Cache.Current;
    internal static DateTimeOffset LastSuccess => Cache.LastSuccess;
    internal static int Version => Cache.Version;

    // The widget binds the one status store it renders. Keeping this explicit avoids a second
    // watcher/store and makes refresh update the live widget's rollout snapshot.
    internal static void Attach(CodexStatusStore statusStore) => _statusStore = statusStore;

    internal static void UpdateFrom(CodexSnapshot? snapshot)
    {
        if (snapshot is not null)
            Cache.Update(snapshot);
    }

    internal static void ForceRefresh() => _statusStore?.ForceRefresh();

    // Compatibility projections let a Claude-shaped renderer consume rollout-derived values while
    // it migrates to Current.Primary and Current.Secondary.
    internal static float FiveHour => Current?.Primary is { } primary ? (float)(primary.UsedPercent / 100) : -1;
    internal static float Week => Current?.Secondary is { } secondary ? (float)(secondary.UsedPercent / 100) : -1;
    internal static DateTimeOffset FiveHourReset => Current?.Primary?.ResetsAt ?? DateTimeOffset.MinValue;
    internal static DateTimeOffset WeekReset => Current?.Secondary?.ResetsAt ?? DateTimeOffset.MinValue;
    internal static void OnPanelOpen() => ForceRefresh();
}
