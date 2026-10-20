using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ObjectRemoverPlugin;

public enum ModelKind { Lama = 0, MiGan = 1 }

public enum ProcessorMode { Auto = 0, Gpu = 1, Cpu = 2 }

internal sealed record EngineOptions(ModelKind Model, ProcessorMode Processor, int GpuAdapter, int Expand, int Feather);

/// <summary>The blended, ready-to-copy result for a rectangular part of the image (straight BGRA).</summary>
internal sealed class InpaintPatch
{
    public InpaintPatch(int x, int y, int width, int height)
    {
        X = x; Y = y; Width = width; Height = height;
        Bgra = new byte[width * height * 4];
    }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }
}

/// <summary>
/// Pre-loads the native ONNX Runtime / DirectML DLLs that ship next to the plugin. Paint.NET loads plugins
/// with its own probing rules, so we load them by full path before ONNX Runtime asks for them by name.
/// (Windows then reuses the already-loaded module, which also prevents the older onnxruntime.dll /
/// DirectML.dll in System32 from being picked up.)
/// </summary>
internal static class OrtBootstrap
{
    private static bool done;

    public static void EnsureLoaded()
    {
        if (done)
        {
            return;
        }

        lock (typeof(OrtBootstrap))
        {
            if (done)
            {
                return;
            }

            string dir = InpaintEngine.PluginDir;
            foreach (string name in new[] { "DirectML.dll", "onnxruntime.dll" })
            {
                string path = System.IO.Path.Combine(dir, name);
                if (System.IO.File.Exists(path))
                {
                    NativeLibrary.TryLoad(path, out _);
                }
            }

            done = true;
        }
    }
}

internal static class InpaintEngine
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> GpuBroken = new();

    private static InferenceSession? session;
    private static ModelKind sessionModel;
    private static bool sessionGpu;
    private static int sessionAdapter;
    private static DateTime lastUse = DateTime.UtcNow;
    private static System.Threading.Timer? idleTimer;
    private static string lastGpuFailure = "";

    /// <summary>Human readable status, polled by the dialog.</summary>
    public static volatile string Status = "Ready";

    public static string PluginDir
    {
        get
        {
            string loc = typeof(InpaintEngine).Assembly.Location;
            if (!string.IsNullOrEmpty(loc))
            {
                return System.IO.Path.GetDirectoryName(loc)!;
            }

            return System.IO.Path.Combine(AppContext.BaseDirectory, "Effects", "ObjectRemover");
        }
    }

    /// <summary>Shared models folder first (%LocalAppData%\paint.net-shared\models), then the legacy Models folder next to the DLL.</summary>
    public static string ModelPath(ModelKind kind) => ModelStore.PathFor(kind);

    // ---------------------------------------------------------------------------------------------
    // Session management
    // ---------------------------------------------------------------------------------------------

    private static InferenceSession CreateSession(string path, bool gpu, int adapter)
    {
        using var so = new SessionOptions();
        so.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        if (gpu)
        {
            // Required by the DirectML execution provider
            so.EnableMemoryPattern = false;
            so.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            so.AppendExecutionProvider_DML(adapter);
        }
        else
        {
            so.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount);
        }

        return new InferenceSession(path, so);
    }

    private static InferenceSession AcquireSession(EngineOptions o, out bool gpu, out string note)
    {
        note = "";
        string path = ModelPath(o.Model);
        if (!System.IO.File.Exists(path))
        {
            throw new System.IO.FileNotFoundException(
                $"Model file not found: {path}. Click OK in the plugin dialog to download the models, or copy them into {ModelStore.SharedDir}.");
        }

        OrtBootstrap.EnsureLoaded();

        string brokenKey = $"{o.Model}:{o.GpuAdapter}";
        bool wantGpu = o.Processor != ProcessorMode.Cpu && !GpuBroken.Contains(brokenKey);

        if (session != null && sessionModel == o.Model && sessionGpu == wantGpu && sessionAdapter == o.GpuAdapter)
        {
            gpu = sessionGpu;
            return session;
        }

        session?.Dispose();
        session = null;

        if (wantGpu)
        {
            InferenceSession? candidate = null;
            try
            {
                Status = "Loading model on GPU (first run is slow)...";
                candidate = CreateSession(path, true, o.GpuAdapter);
                Status = "Testing GPU...";
                SelfTest(candidate, o.Model);
                session = candidate;
                sessionModel = o.Model;
                sessionGpu = true;
                sessionAdapter = o.GpuAdapter;
                StartIdleTimer();
                gpu = true;
                return session;
            }
            catch (Exception ex)
            {
                candidate?.Dispose();
                GpuBroken.Add(brokenKey);
                lastGpuFailure = Short(ex.Message);
            }
        }

        Status = "Loading model on CPU...";
        session = CreateSession(path, false, 0);
        sessionModel = o.Model;
        sessionGpu = false;
        sessionAdapter = o.GpuAdapter;
        StartIdleTimer();
        gpu = false;

        if (o.Processor != ProcessorMode.Cpu)
        {
            note = $" (GPU not usable with this model: {lastGpuFailure}; used CPU)";
        }

        return session;
    }

    private static string Short(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= 120 ? s : s.Substring(0, 120) + "...";
    }

    private static void StartIdleTimer()
    {
        idleTimer ??= new System.Threading.Timer(_ =>
        {
            lock (Gate)
            {
                if (session != null && (DateTime.UtcNow - lastUse).TotalMinutes >= 3)
                {
                    session.Dispose();
                    session = null;
                }
            }
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private static void SelfTest(InferenceSession s, ModelKind model)
    {
        if (model == ModelKind.MiGan)
        {
            const int n = 128;
            var rgb = new byte[n * n * 3];
            var hole = new byte[n * n];
            for (int y = 48; y < 80; y++)
            {
                for (int x = 48; x < 80; x++)
                {
                    hole[y * n + x] = 255;
                }
            }

            RunMiGan(s, rgb, hole, n, n, CancellationToken.None);
        }
        else
        {
            const int n = 512;
            var rgb = new float[n * n * 3];
            var hole = new byte[n * n];
            for (int y = 224; y < 288; y++)
            {
                for (int x = 224; x < 288; x++)
                {
                    hole[y * n + x] = 1;
                }
            }

            RunLama(s, rgb, hole, CancellationToken.None);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Public entry point
    // ---------------------------------------------------------------------------------------------

    /// <param name="srcBgra">Whole source image, straight BGRA, w*h*4 bytes.</param>
    /// <param name="mask">w*h bytes, non-zero = remove.</param>
    /// <param name="bx0">Bounding box of the mask: [bx0,bx1) x [by0,by1).</param>
    public static InpaintPatch Run(byte[] srcBgra, int w, int h, byte[] mask,
        int bx0, int by0, int bx1, int by1, EngineOptions o, CancellationToken ct)
    {
        lock (Gate)
        {
            ct.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();

            Status = "Preparing...";
            InferenceSession s = AcquireSession(o, out bool gpu, out string note);
            lastUse = DateTime.UtcNow;
            ct.ThrowIfCancellationRequested();

            // 1. Pick the window of the image the model will look at
            (int X, int Y, int W, int H) win = PlanWindow(w, h, bx0, by0, bx1, by1, o);

            // 2. Hole mask for that window: user mask grown by 'Expand' px (covers halos / shadows / edges)
            byte[] winMask = ExtractMask(mask, w, h, win);
            byte[] hole = ImageOps.DilateDisc(winMask, win.W, win.H, o.Expand);

            // 3. Blend weights: fully replace the hole, fade out over 'Feather' px outside it
            var weight = new float[win.W * win.H];
            for (int i = 0; i < weight.Length; i++)
            {
                weight[i] = hole[i] != 0 ? 1f : 0f;
            }

            if (o.Feather > 0)
            {
                var blurred = (float[])weight.Clone();
                ImageOps.BoxBlur(blurred, win.W, win.H, Math.Max(1, o.Feather / 2), 2);
                for (int i = 0; i < weight.Length; i++)
                {
                    weight[i] = Math.Max(weight[i], blurred[i]);
                }
            }

            float[] rgb = ExtractRgb(srcBgra, w, h, win);
            ct.ThrowIfCancellationRequested();

            Status = $"Removing object on {(gpu ? "GPU" : "CPU")}...";
            float[] outRgb = o.Model == ModelKind.MiGan
                ? InpaintMiGan(s, rgb, hole, win.W, win.H, ct)
                : InpaintLama(s, rgb, hole, win.W, win.H, ct);

            InpaintPatch patch = Composite(srcBgra, w, h, win, outRgb, weight);

            Status = $"Done in {timer.Elapsed.TotalSeconds:0.0}s on {(gpu ? "GPU (DirectML)" : "CPU")}{note}";
            lastUse = DateTime.UtcNow;
            return patch;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Windowing
    // ---------------------------------------------------------------------------------------------

    private static (int X, int Y, int W, int H) PlanWindow(int w, int h, int bx0, int by0, int bx1, int by1, EngineOptions o)
    {
        int bw = bx1 - bx0, bh = by1 - by0;
        int big = Math.Max(bw, bh);
        int pad = o.Expand + o.Feather + 2;

        if (o.Model == ModelKind.MiGan)
        {
            // MI-GAN's ONNX pipeline crops/resizes internally, so any window size works.
            int m = Math.Clamp((int)(0.75 * big), 64, 384) + pad;
            int x0 = Math.Max(0, bx0 - m), y0 = Math.Max(0, by0 - m);
            int x1 = Math.Min(w, bx1 + m), y1 = Math.Min(h, by1 + m);
            return (x0, y0, x1 - x0, y1 - y0);
        }
        else
        {
            // LaMa: fixed 512x512 input. Use a square window around the object with generous context.
            // Small objects get a window of at least 512 px so the model runs at 1:1 scale (best detail).
            int m = Math.Clamp(big / 2, 96, 256) + pad;
            int side = Math.Max(big + 2 * m, 512);
            side = Math.Min(side, Math.Max(w, h));
            int cx = (bx0 + bx1) / 2, cy = (by0 + by1) / 2;
            int x0 = cx - side / 2, y0 = cy - side / 2;
            if (side <= w) { x0 = Math.Clamp(x0, 0, w - side); }
            if (side <= h) { y0 = Math.Clamp(y0, 0, h - side); }
            return (x0, y0, side, side);
        }
    }

    private static byte[] ExtractMask(byte[] mask, int w, int h, (int X, int Y, int W, int H) win)
    {
        var res = new byte[win.W * win.H];
        for (int y = 0; y < win.H; y++)
        {
            int gy = win.Y + y;
            if ((uint)gy >= (uint)h)
            {
                continue;
            }

            int xs = Math.Max(0, -win.X);
            int xe = Math.Min(win.W, w - win.X);
            for (int x = xs; x < xe; x++)
            {
                res[y * win.W + x] = mask[gy * w + win.X + x] != 0 ? (byte)255 : (byte)0;
            }
        }

        return res;
    }

    // RGB (0..255) as interleaved floats. Pixels outside the image repeat the nearest edge pixel.
    private static float[] ExtractRgb(byte[] srcBgra, int w, int h, (int X, int Y, int W, int H) win)
    {
        var rgb = new float[win.W * win.H * 3];
        Parallel.For(0, win.H, y =>
        {
            int sy = Math.Clamp(win.Y + y, 0, h - 1);
            for (int x = 0; x < win.W; x++)
            {
                int sx = Math.Clamp(win.X + x, 0, w - 1);
                int si = (sy * w + sx) * 4;
                int di = (y * win.W + x) * 3;
                rgb[di] = srcBgra[si + 2];
                rgb[di + 1] = srcBgra[si + 1];
                rgb[di + 2] = srcBgra[si];
            }
        });
        return rgb;
    }

    // ---------------------------------------------------------------------------------------------
    // Model specific paths
    // ---------------------------------------------------------------------------------------------

    private static float[] InpaintMiGan(InferenceSession s, float[] rgb, byte[] hole, int w, int h, CancellationToken ct)
    {
        var rgb8 = new byte[rgb.Length];
        for (int i = 0; i < rgb.Length; i++)
        {
            rgb8[i] = (byte)Math.Clamp((int)(rgb[i] + 0.5f), 0, 255);
        }

        return RunMiGan(s, rgb8, hole, w, h, ct);
    }

    private static float[] InpaintLama(InferenceSession s, float[] rgb, byte[] hole, int w, int h, CancellationToken ct)
    {
        const int n = 512;
        float[] in512 = ImageOps.Resize(rgb, w, h, 3, n, n);

        var maskF = new float[w * h];
        for (int i = 0; i < maskF.Length; i++)
        {
            maskF[i] = hole[i] != 0 ? 1f : 0f;
        }

        float[] mask512 = ImageOps.Resize(maskF, w, h, 1, n, n);
        float thr = w > n ? 0.02f : 0.5f; // keep thin masks alive when shrinking
        var hole512 = new byte[n * n];
        for (int i = 0; i < hole512.Length; i++)
        {
            hole512[i] = mask512[i] > thr ? (byte)1 : (byte)0;
        }

        float[] out512 = RunLama(s, in512, hole512, ct);
        return ImageOps.Resize(out512, n, n, 3, w, h);
    }

    private static (string Image, string Mask) InputNames(InferenceSession s)
    {
        string[] names = s.InputMetadata.Keys.ToArray();
        string maskName = names.FirstOrDefault(n => n.Contains("mask", StringComparison.OrdinalIgnoreCase)) ?? names[^1];
        string imageName = names.First(n => n != maskName);
        return (imageName, maskName);
    }

    /// <summary>MI-GAN pipeline v2: image uint8 [1,3,H,W] RGB, mask uint8 [1,1,H,W] (0 = hole, 255 = keep).</summary>
    private static float[] RunMiGan(InferenceSession s, byte[] rgbHwc, byte[] hole, int w, int h, CancellationToken ct)
    {
        int plane = w * h;
        var img = new byte[3 * plane];
        var m = new byte[plane];
        for (int i = 0; i < plane; i++)
        {
            img[i] = rgbHwc[i * 3];
            img[plane + i] = rgbHwc[i * 3 + 1];
            img[2 * plane + i] = rgbHwc[i * 3 + 2];
            m[i] = hole[i] != 0 ? (byte)0 : (byte)255;
        }

        (string imageName, string maskName) = InputNames(s);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(imageName, new DenseTensor<byte>(img, new[] { 1, 3, h, w })),
            NamedOnnxValue.CreateFromTensor(maskName, new DenseTensor<byte>(m, new[] { 1, 1, h, w })),
        };

        byte[] o = RunModel<byte>(s, inputs, ct);

        var outRgb = new float[plane * 3];
        for (int i = 0; i < plane; i++)
        {
            outRgb[i * 3] = o[i];
            outRgb[i * 3 + 1] = o[plane + i];
            outRgb[i * 3 + 2] = o[2 * plane + i];
        }

        return outRgb;
    }

    /// <summary>LaMa (Carve export): image float32 [1,3,512,512] in 0..1, mask float32 [1,1,512,512] (1 = hole).
    /// Output float32 [1,3,512,512] already in 0..255.</summary>
    private static float[] RunLama(InferenceSession s, float[] rgb512Hwc, byte[] hole512, CancellationToken ct)
    {
        const int n = 512;
        const int plane = n * n;
        var img = new float[3 * plane];
        var m = new float[plane];
        for (int i = 0; i < plane; i++)
        {
            if (hole512[i] != 0)
            {
                m[i] = 1f; // image stays 0 under the hole
                continue;
            }

            img[i] = rgb512Hwc[i * 3] / 255f;
            img[plane + i] = rgb512Hwc[i * 3 + 1] / 255f;
            img[2 * plane + i] = rgb512Hwc[i * 3 + 2] / 255f;
        }

        (string imageName, string maskName) = InputNames(s);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(imageName, new DenseTensor<float>(img, new[] { 1, 3, n, n })),
            NamedOnnxValue.CreateFromTensor(maskName, new DenseTensor<float>(m, new[] { 1, 1, n, n })),
        };

        float[] o = RunModel<float>(s, inputs, ct);

        var outRgb = new float[plane * 3];
        for (int i = 0; i < plane; i++)
        {
            outRgb[i * 3] = o[i];
            outRgb[i * 3 + 1] = o[plane + i];
            outRgb[i * 3 + 2] = o[2 * plane + i];
        }

        return outRgb;
    }

    private static T[] RunModel<T>(InferenceSession s, List<NamedOnnxValue> inputs, CancellationToken ct)
    {
        using var runOptions = new RunOptions();
        using CancellationTokenRegistration reg = ct.Register(() => runOptions.Terminate = true);
        string[] outputNames = s.OutputMetadata.Keys.Take(1).ToArray();
        try
        {
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = s.Run(inputs, outputNames, runOptions);
            Tensor<T> tensor = results.First().AsTensor<T>();
            return tensor is DenseTensor<T> dense ? dense.Buffer.ToArray() : tensor.ToArray();
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Compositing
    // ---------------------------------------------------------------------------------------------

    private static InpaintPatch Composite(byte[] src, int w, int h, (int X, int Y, int W, int H) win,
        float[] outRgb, float[] weight)
    {
        int px0 = Math.Max(0, win.X), py0 = Math.Max(0, win.Y);
        int px1 = Math.Min(w, win.X + win.W), py1 = Math.Min(h, win.Y + win.H);
        var patch = new InpaintPatch(px0, py0, px1 - px0, py1 - py0);
        byte[] dst = patch.Bgra;

        Parallel.For(py0, py1, y =>
        {
            for (int x = px0; x < px1; x++)
            {
                int si = (y * w + x) * 4;
                int wi = (y - win.Y) * win.W + (x - win.X);
                int di = ((y - py0) * patch.Width + (x - px0)) * 4;
                float a = weight[wi];

                if (a <= 0.003f)
                {
                    dst[di] = src[si];
                    dst[di + 1] = src[si + 1];
                    dst[di + 2] = src[si + 2];
                    dst[di + 3] = src[si + 3];
                    continue;
                }

                int oi = wi * 3;
                float inv = 1f - a;
                dst[di] = ToByte(src[si] * inv + outRgb[oi + 2] * a);
                dst[di + 1] = ToByte(src[si + 1] * inv + outRgb[oi + 1] * a);
                dst[di + 2] = ToByte(src[si + 2] * inv + outRgb[oi] * a);
                // Filled pixels become opaque (an object on a transparent layer is replaced by fill)
                dst[di + 3] = ToByte(src[si + 3] + (255 - src[si + 3]) * a);
            }
        });

        return patch;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
}
