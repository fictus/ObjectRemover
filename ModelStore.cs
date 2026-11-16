using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace ObjectRemoverPlugin;

/// <param name="MinBytes">Any file smaller than this is certainly not the real model (e.g. a firewall's HTML block page).</param>
internal sealed record ModelInfo(ModelKind Kind, string DisplayName, string FileName, string Url, string ApproxSize, long MinBytes);

/// <summary>A download problem with a message that is safe and useful to show to the user as-is.</summary>
internal sealed class ModelDownloadException : System.IO.IOException
{
    public ModelDownloadException(string message, Exception? inner = null) : base(message, inner) { }
}

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
            "https://huggingface.co/Carve/LaMa-ONNX/resolve/main/lama_fp32.onnx", "about 208 MB", 100_000_000),
        new(ModelKind.MiGan, "MI-GAN (fast)", "migan_pipeline_v2.onnx",
            "https://huggingface.co/andraniksargsyan/migan/resolve/main/migan_pipeline_v2.onnx", "about 28 MB", 10_000_000),
    };

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // Redirects are followed by hand (see SendFollowingRedirectsAsync) so every hop can be logged:
        // huggingface.co answers with a redirect to a separate CDN host, and that host is the one firewalls often block.
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials, // corporate proxies that use Windows sign-in
        };
        var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PaintDotNet-ObjectRemover/1.0");
        return c;
    }

    /// <summary>Give up if the server sends no response headers / no data at all for this long.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    // ---------------------------------------------------------------------------------------------
    // Locations
    // ---------------------------------------------------------------------------------------------

    public static string SharedDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "paint.net-shared", "models");

    public static ModelInfo Info(ModelKind kind) => All.First(m => m.Kind == kind);

    private static string SharedPath(ModelInfo m) => System.IO.Path.Combine(SharedDir, m.FileName);

    private static string LegacyPath(ModelInfo m) => System.IO.Path.Combine(InpaintEngine.PluginDir, "Models", m.FileName);

    /// <summary>True if the file exists and is big enough to plausibly be the real model.</summary>
    private static bool IsComplete(string path, ModelInfo m)
    {
        try
        {
            var fi = new System.IO.FileInfo(path);
            return fi.Exists && fi.Length >= m.MinBytes;
        }
        catch (System.IO.IOException)
        {
            return false;
        }
    }

    /// <summary>Path to load the model from: shared folder first, then the legacy plugin folder.
    /// If neither holds a complete file, the shared path is returned (that is where a download will put it).</summary>
    public static string PathFor(ModelKind kind)
    {
        ModelInfo m = Info(kind);
        string shared = SharedPath(m);
        if (IsComplete(shared, m))
        {
            return shared;
        }

        string legacy = LegacyPath(m);
        return IsComplete(legacy, m) ? legacy : shared;
    }

    /// <summary>True only for a model file that is present AND plausibly complete (a 3 KB error page does not count).</summary>
    public static bool Exists(ModelKind kind)
    {
        ModelInfo m = Info(kind);
        return IsComplete(PathFor(kind), m);
    }

    public static List<ModelInfo> Missing() => All.Where(m => !Exists(m.Kind)).ToList();

    /// <summary>Size in bytes of a file that sits at the model's location but is too small to be the model, or null.</summary>
    public static long? IncompleteFileSize(ModelInfo m)
    {
        try
        {
            var fi = new System.IO.FileInfo(SharedPath(m));
            return fi.Exists && fi.Length < m.MinBytes ? fi.Length : null;
        }
        catch (System.IO.IOException)
        {
            return null;
        }
    }

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
        PluginLog.Info($"Model download requested for: {string.Join(", ", models.Select(m => m.FileName))}");
        System.IO.Directory.CreateDirectory(SharedDir);
        for (int i = 0; i < models.Count; i++)
        {
            await DownloadOneAsync(models[i], i, models.Count, progress, ct).ConfigureAwait(false);
        }

        PluginLog.Info("Model download finished.");
    }

    private static async Task DownloadOneAsync(ModelInfo m, int index, int count, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        string final = SharedPath(m);
        long? bad = IncompleteFileSize(m);
        if (bad.HasValue)
        {
            // This is what a firewall "block page" saved under the model's name looks like. Previously it was accepted as the model.
            PluginLog.Warn($"{m.FileName} exists but is only {bad.Value:N0} bytes (a real one is at least {m.MinBytes:N0}). Content starts: {FileSnippet(final)}. Deleting it and downloading again.");
            TryDelete(final);
        }

        if (System.IO.File.Exists(final))
        {
            PluginLog.Info($"{m.FileName} is already present ({new System.IO.FileInfo(final).Length:N0} bytes); skipping.");
            return;
        }

        string part = final + ".part";
        string meta = final + ".part.meta"; // remembers the ETag so we never glue together two different versions
        PluginLog.Info($"Downloading {m.DisplayName}: {m.Url} -> {final}");
        try
        {
            PluginLog.Info($"  Proxy used for this URL: {(HttpClient.DefaultProxy.GetProxy(new Uri(m.Url))?.ToString() ?? "(none, direct connection)")}");
        }
        catch
        {
        }

        // FileShare.Read: a second process (another plugin) trying to download the same file fails fast instead of corrupting it.
        System.IO.FileStream fs;
        try
        {
            fs = new System.IO.FileStream(part, System.IO.FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, System.IO.FileShare.Read);
        }
        catch (System.IO.IOException ex)
        {
            PluginLog.Error($"Couldn't open {part}", ex);
            throw new System.IO.IOException($"{m.FileName} is already being downloaded by another Paint.NET plugin. Try again in a moment.");
        }

        long total = -1;
        bool discardPartial = false; // set when what we received is junk (e.g. a block page) so it must not be resumed from
        try
        {
            using (fs)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    long existing = fs.Length;
                    EntityTagHeaderValue? saved = existing > 0 ? ReadEtag(meta) : null;
                    if (existing > 0)
                    {
                        PluginLog.Info($"  Resuming from byte {existing:N0}");
                    }

                    (HttpResponseMessage resp, Uri finalUri) = await SendFollowingRedirectsAsync(m, existing, saved, ct).ConfigureAwait(false);
                    using (resp)
                    {
                        string? mediaType = resp.Content.Headers.ContentType?.MediaType;
                        long? contentLength = resp.Content.Headers.ContentLength;
                        PluginLog.Info($"  Final response from {finalUri.Host}: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}, Content-Type={mediaType ?? "(none)"}, Content-Length={(contentLength.HasValue ? contentLength.Value.ToString("N0") : "(none)")}");

                        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                        {
                            long? full = resp.Content.Headers.ContentRange?.Length;
                            if (full.HasValue && full.Value == existing)
                            {
                                total = existing; // the partial file is actually complete
                                break;
                            }

                            PluginLog.Info("  Server rejected the resume range; starting over.");
                            fs.SetLength(0); // stale or oversized partial: start over
                            continue;
                        }

                        if (!resp.IsSuccessStatusCode)
                        {
                            string body = await ReadSnippetAsync(resp, ct).ConfigureAwait(false);
                            PluginLog.Error($"  Server error body: {body}");
                            discardPartial = existing == 0;
                            throw new ModelDownloadException(
                                $"{finalUri.Host} answered \"{(int)resp.StatusCode} {resp.ReasonPhrase}\" instead of sending the file." + DescribeBody(body) +
                                " If you use a firewall, proxy or content filter, it may be blocking this server.");
                        }

                        bool partial = resp.StatusCode == HttpStatusCode.PartialContent;
                        bool looksLikeWebPage = mediaType != null &&
                            (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                             mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase));
                        bool tooSmall = !partial && contentLength.HasValue && contentLength.Value < m.MinBytes;

                        if (looksLikeWebPage || tooSmall)
                        {
                            // This is the reported bug: a firewall/proxy replies 200 OK with a tiny HTML page. Don't save it as the model.
                            string body = await ReadSnippetAsync(resp, ct).ConfigureAwait(false);
                            PluginLog.Error($"  The response is not the model file (Content-Type {mediaType}, {contentLength:N0} bytes; expected at least {m.MinBytes:N0}). Body starts: {body}");
                            discardPartial = true;
                            throw new ModelDownloadException(
                                $"{finalUri.Host} sent a small web page ({(contentLength.HasValue ? contentLength.Value.ToString("N0") + " bytes" : mediaType)}) instead of the model file." + DescribeBody(body) +
                                " A firewall, proxy or content filter is most likely intercepting the download. Allow access to huggingface.co and " + finalUri.Host + ", then try again.");
                        }

                        if (partial && existing > 0)
                        {
                            ContentRangeHeaderValue? cr = resp.Content.Headers.ContentRange;
                            if (cr?.From != existing)
                            {
                                PluginLog.Info("  Server resumed from a different offset; starting over.");
                                fs.SetLength(0);
                                continue;
                            }

                            total = cr.Length ?? (existing + (contentLength ?? 0));
                        }
                        else
                        {
                            // 200: fresh download, or the server ignored/refused the range (e.g. the file changed): restart from zero
                            fs.SetLength(0);
                            existing = 0;
                            total = contentLength ?? -1;
                            WriteEtag(meta, resp.Headers.ETag);
                        }

                        fs.Seek(existing, System.IO.SeekOrigin.Begin);
                        await CopyAsync(resp, fs, m, index, count, existing, total, progress, finalUri, ct).ConfigureAwait(false);
                        break;
                    }
                }

                await fs.FlushAsync(ct).ConfigureAwait(false);
                if (total >= 0 && fs.Length != total)
                {
                    PluginLog.Error($"  Download of {m.FileName} ended early: {fs.Length:N0} of {total:N0} bytes.");
                    throw new ModelDownloadException($"The download of {m.FileName} ended early ({fs.Length:N0} of {total:N0} bytes). Your progress is saved and will resume next time.");
                }

                if (fs.Length < m.MinBytes)
                {
                    string head = StreamSnippet(fs);
                    PluginLog.Error($"  Received only {fs.Length:N0} bytes for {m.FileName} (expected at least {m.MinBytes:N0}). Content starts: {head}");
                    discardPartial = true;
                    throw new ModelDownloadException($"The server returned only {fs.Length:N0} bytes for {m.FileName}, which is too small to be the model." + DescribeBody(head));
                }

                if (LooksLikeHtml(fs))
                {
                    string head = StreamSnippet(fs);
                    PluginLog.Error($"  {m.FileName} starts with HTML, not model data. Content starts: {head}");
                    discardPartial = true;
                    throw new ModelDownloadException($"What was downloaded for {m.FileName} is a web page, not the model." + DescribeBody(head));
                }
            }
        }
        finally
        {
            if (discardPartial)
            {
                TryDelete(part);
                TryDelete(meta);
            }
        }

        System.IO.File.Move(part, final, overwrite: true);
        TryDelete(meta);
        PluginLog.Info($"Downloaded {m.FileName} OK ({new System.IO.FileInfo(final).Length:N0} bytes).");
        progress?.Report(new DownloadProgress(index, count, m.DisplayName, total, total));
    }

    /// <summary>
    /// Sends the request, following redirects manually so each hop is logged and a failure names the host that caused it.
    /// </summary>
    private static async Task<(HttpResponseMessage Response, Uri FinalUri)> SendFollowingRedirectsAsync(
        ModelInfo m, long existing, EntityTagHeaderValue? etag, CancellationToken ct)
    {
        Uri uri = new(m.Url);
        for (int hop = 0; hop < 8; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            if (existing > 0)
            {
                req.Headers.Range = new RangeHeaderValue(existing, null);
                if (etag != null)
                {
                    req.Headers.IfRange = new RangeConditionHeaderValue(etag);
                }
            }

            PluginLog.Info($"  GET {uri}");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(StallTimeout);

            HttpResponseMessage resp;
            try
            {
                resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                PluginLog.Error($"  No response from {uri.Host} within {StallTimeout.TotalSeconds:0} seconds.");
                throw new ModelDownloadException($"{uri.Host} did not respond within {StallTimeout.TotalSeconds:0} seconds. A firewall may be silently blocking it.");
            }
            catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException)
            {
                PluginLog.Error($"  Request to {uri.Host} failed.", ex);
                string hint = PluginLog.NetworkHint(ex);
                throw new ModelDownloadException(
                    $"Couldn't connect to {uri.Host}. {PluginLog.RootMessage(ex)}" + (hint.Length > 0 ? " " + hint : " Check your internet connection and any firewall or proxy."), ex);
            }

            int code = (int)resp.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && resp.Headers.Location != null)
            {
                Uri next = resp.Headers.Location.IsAbsoluteUri ? resp.Headers.Location : new Uri(uri, resp.Headers.Location);
                PluginLog.Info($"  HTTP {code} redirect from {uri.Host} to {next.Host}");
                resp.Dispose();
                uri = next;
                continue;
            }

            return (resp, uri);
        }

        throw new ModelDownloadException("The server redirected too many times.");
    }

    private static async Task CopyAsync(HttpResponseMessage resp, System.IO.FileStream fs, ModelInfo m, int index, int count,
        long existing, long total, IProgress<DownloadProgress>? progress, Uri source, CancellationToken ct)
    {
        await using System.IO.Stream src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[128 * 1024];
        long done = existing;
        long lastReport = Environment.TickCount64;
        int lastLoggedPct = -1;
        progress?.Report(new DownloadProgress(index, count, m.DisplayName, done, total));

        try
        {
            int n;
            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(StallTimeout);
                try
                {
                    n = await src.ReadAsync(buffer.AsMemory(), stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    PluginLog.Error($"  No data from {source.Host} for {StallTimeout.TotalSeconds:0}s at {done:N0} bytes.");
                    throw new ModelDownloadException($"The download from {source.Host} stalled (no data for {StallTimeout.TotalSeconds:0} seconds). Your progress is saved and will resume next time.");
                }

                if (n <= 0)
                {
                    break;
                }

                await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;

                long now = Environment.TickCount64;
                if (now - lastReport >= 200)
                {
                    lastReport = now;
                    progress?.Report(new DownloadProgress(index, count, m.DisplayName, done, total));
                }

                if (total > 0)
                {
                    int pct = (int)(done * 10 / total) * 10; // log every 10%
                    if (pct != lastLoggedPct)
                    {
                        lastLoggedPct = pct;
                        PluginLog.Info($"  {m.FileName}: {done:N0} / {total:N0} bytes ({pct}%)");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException and not ModelDownloadException || ex is HttpRequestException)
        {
            PluginLog.Error($"  Connection to {source.Host} dropped at {done:N0} bytes.", ex);
            string hint = PluginLog.NetworkHint(ex);
            throw new ModelDownloadException(
                $"The connection to {source.Host} was lost after {done:N0} bytes. {PluginLog.RootMessage(ex)} {hint} Your progress is saved and will resume next time.".Replace("  ", " "), ex);
        }
        finally
        {
            // Whatever happened (cancel, dropped connection), keep everything received so far for the next attempt
            try { await fs.FlushAsync(CancellationToken.None).ConfigureAwait(false); } catch (System.IO.IOException) { }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers for recognising and describing "not actually a model" content
    // ---------------------------------------------------------------------------------------------

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            await using System.IO.Stream s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buf = new byte[4096];
            int n = 0;
            int r;
            while (n < buf.Length && (r = await s.ReadAsync(buf.AsMemory(n), ct).ConfigureAwait(false)) > 0)
            {
                n += r;
            }

            return Clean(Encoding.UTF8.GetString(buf, 0, n));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "(couldn't read the response body: " + ex.Message + ")";
        }
    }

    private static string StreamSnippet(System.IO.FileStream fs)
    {
        try
        {
            var buf = new byte[Math.Min(4096, fs.Length)];
            fs.Seek(0, System.IO.SeekOrigin.Begin);
            int n = fs.Read(buf, 0, buf.Length);
            return Clean(Encoding.UTF8.GetString(buf, 0, n));
        }
        catch (System.IO.IOException)
        {
            return "(unreadable)";
        }
    }

    private static string FileSnippet(string path)
    {
        try
        {
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            return StreamSnippet(fs);
        }
        catch (System.IO.IOException)
        {
            return "(unreadable)";
        }
    }

    /// <summary>True when the file starts with an HTML/XML document instead of binary model data.</summary>
    private static bool LooksLikeHtml(System.IO.FileStream fs)
    {
        try
        {
            var buf = new byte[512];
            fs.Seek(0, System.IO.SeekOrigin.Begin);
            int n = fs.Read(buf, 0, buf.Length);
            string head = Encoding.UTF8.GetString(buf, 0, n).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            return head.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                   head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || head.StartsWith("{\"", StringComparison.Ordinal);
        }
        catch (System.IO.IOException)
        {
            return false;
        }
    }

    private static string Clean(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        s = new string(s.Where(c => !char.IsControl(c)).ToArray());
        return s.Length <= 1500 ? s : s.Substring(0, 1500) + "...";
    }

    /// <summary>Turns a response body into a short sentence for the user, using the page title if there is one.</summary>
    private static string DescribeBody(string body)
    {
        Match t = Regex.Match(body, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (t.Success && t.Groups[1].Value.Trim().Length > 0)
        {
            return $" The page says: \"{t.Groups[1].Value.Trim()}\".";
        }

        string plain = Regex.Replace(body, "<[^>]+>", " ");
        plain = Regex.Replace(plain, @"\s+", " ").Trim();
        if (plain.Length == 0 || plain.StartsWith("(", StringComparison.Ordinal))
        {
            return "";
        }

        return " The response said: \"" + (plain.Length <= 200 ? plain : plain.Substring(0, 200) + "...") + "\".";
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
