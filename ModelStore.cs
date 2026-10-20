using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace ObjectRemoverPlugin;

internal sealed record ModelInfo(ModelKind Kind, string DisplayName, string FileName, string Url, string ApproxSize);

internal readonly record struct DownloadProgress(int FileIndex, int FileCount, string Name, long Done, long Total);

/// <summary>
/// Locates the AI model files and downloads missing ones.
/// Models live in %LocalAppData%\paint.net-shared\models\ so that other Paint.NET plugins can share them.
/// For backwards compatibility a model found in the old "Models" folder next to the plugin DLL is still used.
/// Downloads are resumable: data is written to "&lt;file&gt;.part" and continued with an HTTP Range request.
/// </summary>
internal static class ModelStore
{
    // API-CHECK: confirm these direct-download URLs still resolve (they correspond to the pages listed in the README).
    public static readonly ModelInfo[] All =
    {
        new(ModelKind.Lama, "LaMa (best quality)", "lama_fp32.onnx",
            "https://huggingface.co/Carve/LaMa-ONNX/resolve/main/lama_fp32.onnx", "about 208 MB"),
        new(ModelKind.MiGan, "MI-GAN (fast)", "migan_pipeline_v2.onnx",
            "https://huggingface.co/andraniksargsyan/migan/resolve/main/migan_pipeline_v2.onnx", "about 28 MB"),
    };

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PaintDotNet-ObjectRemover/1.0");
        return c;
    }

    // ---------------------------------------------------------------------------------------------
    // Locations
    // ---------------------------------------------------------------------------------------------

    public static string SharedDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "paint.net-shared", "models");

    public static ModelInfo Info(ModelKind kind) => All.First(m => m.Kind == kind);

    private static string SharedPath(ModelInfo m) => System.IO.Path.Combine(SharedDir, m.FileName);

    private static string LegacyPath(ModelInfo m) => System.IO.Path.Combine(InpaintEngine.PluginDir, "Models", m.FileName);

    /// <summary>Path to load the model from: shared folder first, then the legacy plugin folder.
    /// If neither exists, the shared path is returned (that is where a download will put it).</summary>
    public static string PathFor(ModelKind kind)
    {
        ModelInfo m = Info(kind);
        string shared = SharedPath(m);
        if (System.IO.File.Exists(shared))
        {
            return shared;
        }

        string legacy = LegacyPath(m);
        return System.IO.File.Exists(legacy) ? legacy : shared;
    }

    public static bool Exists(ModelKind kind) => System.IO.File.Exists(PathFor(kind));

    public static List<ModelInfo> Missing() => All.Where(m => !Exists(m.Kind)).ToList();

    /// <summary>Bytes already downloaded into a partial file, or 0.</summary>
    public static long PartialBytes(ModelInfo m)
    {
        try
        {
            var fi = new System.IO.FileInfo(SharedPath(m) + ".part");
            return fi.Exists ? fi.Length : 0;
        }
        catch (System.IO.IOException)
        {
            return 0;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Download
    // ---------------------------------------------------------------------------------------------

    public static async Task DownloadAsync(IReadOnlyList<ModelInfo> models, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(SharedDir);
        for (int i = 0; i < models.Count; i++)
        {
            await DownloadOneAsync(models[i], i, models.Count, progress, ct).ConfigureAwait(false);
        }
    }

    private static async Task DownloadOneAsync(ModelInfo m, int index, int count, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        string final = SharedPath(m);
        if (System.IO.File.Exists(final))
        {
            return;
        }

        string part = final + ".part";
        string meta = final + ".part.meta"; // remembers the ETag so we never glue together two different versions

        // FileShare.Read: a second process (another plugin) trying to download the same file fails fast instead of corrupting it.
        System.IO.FileStream fs;
        try
        {
            fs = new System.IO.FileStream(part, System.IO.FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, System.IO.FileShare.Read);
        }
        catch (System.IO.IOException)
        {
            throw new System.IO.IOException($"{m.FileName} is already being downloaded by another Paint.NET plugin. Try again in a moment.");
        }

        long total = -1;
        using (fs)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                long existing = fs.Length;
                using var req = new HttpRequestMessage(HttpMethod.Get, m.Url);
                if (existing > 0)
                {
                    req.Headers.Range = new RangeHeaderValue(existing, null);
                    EntityTagHeaderValue? saved = ReadEtag(meta);
                    if (saved != null)
                    {
                        req.Headers.IfRange = new RangeConditionHeaderValue(saved);
                    }
                }

                using HttpResponseMessage resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    long? full = resp.Content.Headers.ContentRange?.Length;
                    if (full.HasValue && full.Value == existing)
                    {
                        total = existing; // the partial file is actually complete
                        break;
                    }

                    fs.SetLength(0); // stale or oversized partial: start over
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    throw new System.IO.IOException($"{m.DisplayName}: the server returned {(int)resp.StatusCode} {resp.ReasonPhrase} for {m.Url}");
                }

                if (resp.StatusCode == HttpStatusCode.PartialContent && existing > 0)
                {
                    ContentRangeHeaderValue? cr = resp.Content.Headers.ContentRange;
                    if (cr?.From != existing)
                    {
                        fs.SetLength(0);
                        continue;
                    }

                    total = cr.Length ?? (existing + (resp.Content.Headers.ContentLength ?? 0));
                }
                else
                {
                    // 200: fresh download, or the server ignored/refused the range (e.g. the file changed): restart from zero
                    fs.SetLength(0);
                    existing = 0;
                    total = resp.Content.Headers.ContentLength ?? -1;
                    WriteEtag(meta, resp.Headers.ETag);
                }

                fs.Seek(existing, System.IO.SeekOrigin.Begin);
                await CopyAsync(resp, fs, m, index, count, existing, total, progress, ct).ConfigureAwait(false);
                break;
            }

            await fs.FlushAsync(ct).ConfigureAwait(false);
            if (total >= 0 && fs.Length != total)
            {
                throw new System.IO.IOException($"The download of {m.FileName} ended early ({fs.Length:N0} of {total:N0} bytes). Your progress is saved and will resume next time.");
            }

            if (fs.Length == 0)
            {
                throw new System.IO.IOException($"The server returned no data for {m.FileName}.");
            }
        }

        System.IO.File.Move(part, final, overwrite: true);
        TryDelete(meta);
        progress?.Report(new DownloadProgress(index, count, m.DisplayName, total, total));
    }

    private static async Task CopyAsync(HttpResponseMessage resp, System.IO.FileStream fs, ModelInfo m, int index, int count,
        long existing, long total, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        await using System.IO.Stream src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[128 * 1024];
        long done = existing;
        long lastReport = Environment.TickCount64;
        progress?.Report(new DownloadProgress(index, count, m.DisplayName, done, total));

        try
        {
            int n;
            while ((n = await src.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;

                long now = Environment.TickCount64;
                if (now - lastReport >= 200)
                {
                    lastReport = now;
                    progress?.Report(new DownloadProgress(index, count, m.DisplayName, done, total));
                }
            }
        }
        finally
        {
            // Whatever happened (cancel, dropped connection), keep everything received so far for the next attempt
            try { await fs.FlushAsync(CancellationToken.None).ConfigureAwait(false); } catch (System.IO.IOException) { }
        }
    }

    private static EntityTagHeaderValue? ReadEtag(string metaPath)
    {
        try
        {
            if (System.IO.File.Exists(metaPath) && EntityTagHeaderValue.TryParse(System.IO.File.ReadAllText(metaPath).Trim(), out EntityTagHeaderValue? tag) && !tag.IsWeak)
            {
                return tag; // If-Range only accepts strong validators
            }
        }
        catch (System.IO.IOException)
        {
        }

        return null;
    }

    private static void WriteEtag(string metaPath, EntityTagHeaderValue? tag)
    {
        try
        {
            if (tag != null && !tag.IsWeak)
            {
                System.IO.File.WriteAllText(metaPath, tag.ToString());
            }
            else
            {
                TryDelete(metaPath);
            }
        }
        catch (System.IO.IOException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try { System.IO.File.Delete(path); } catch (System.IO.IOException) { }
    }
}
