namespace ObjectRemoverPlugin;

internal sealed class CacheEntry
{
    public string Key = "";
    public string RunKey = "";
    public InpaintPatch? Patch;
    public string? Error;
    public int Version;
}

/// <summary>
/// Paint.NET renders an effect in tiles, possibly on several threads, and re-renders for the final apply.
/// The neural network must run only once per (mask, settings, image), so the finished patch is cached here.
/// </summary>
internal static class ResultCache
{
    /// <summary>Held while the model runs, so only one tile thread computes while the others wait.</summary>
    public static readonly object ComputeGate = new();

    private static readonly object StateLock = new();
    private static CacheEntry? latest;
    private static int version;

    public static bool TryGet(string key, out CacheEntry entry)
    {
        lock (StateLock)
        {
            if (latest != null && latest.Key == key)
            {
                entry = latest;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    public static CacheEntry? Latest
    {
        get
        {
            lock (StateLock)
            {
                return latest;
            }
        }
    }

    public static void Clear()
    {
        lock (StateLock)
        {
            latest = null;
        }
    }

    public static void Store(string key, string runKey, InpaintPatch? patch, string? error)
    {
        lock (StateLock)
        {
            latest = new CacheEntry { Key = key, RunKey = runKey, Patch = patch, Error = error, Version = ++version };
        }
    }
}
