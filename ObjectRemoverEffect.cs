using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using PaintDotNet;
using PaintDotNet.Effects;
using PaintDotNet.Imaging;
using PaintDotNet.Rendering;

namespace ObjectRemoverPlugin;

// API-CHECK: the generic attribute and PluginSupportInfo shape follow what CodeLab generates for Paint.NET 5.x.
public sealed class PluginSupportInfo : IPluginSupportInfo
{
    public string Author => GetType().Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";
    public string Copyright => GetType().Assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? "";
    public string DisplayName => GetType().Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "AI Object Remover";
    public Version Version => GetType().Assembly.GetName().Version ?? new Version(1, 0);
    public Uri WebsiteUri => new Uri("https://www.getpaint.net/redirect/plugins.html");
}

/// <summary>
/// Effects &gt; Photo &gt; AI Object Remover.
/// The dialog lets the user paint a mask with a resizable brush. The renderer runs the inpainting network once
/// (LaMa or MI-GAN through ONNX Runtime, DirectML GPU or CPU) and then copies the result tile by tile.
/// </summary>
[PluginSupportInfo<PluginSupportInfo>(DisplayName = "AI Object Remover")]
public sealed class ObjectRemoverEffect : BitmapEffect<ObjectRemoverToken>
{
    // API-CHECK: BitmapEffectOptions.Create() with { IsConfigurable = true } as used by the official samples.
    public ObjectRemoverEffect()
        : base("AI Object Remover", CreateIcon(), "Photo", BitmapEffectOptions.Create() with { IsConfigurable = true })
    {
    }

    protected override IEffectConfigForm OnCreateConfigForm() => new ObjectRemoverConfigDialog();

    protected override void OnRender(IBitmapEffectOutput output)
    {
        RectInt32 bounds = output.Bounds;
        ObjectRemoverToken? token = Token;

        using IEffectInputBitmap<ColorBgra32> srcBitmap = Environment.GetSourceBitmapBgra32();
        int w = srcBitmap.Size.Width;
        int h = srcBitmap.Size.Height;
        using IBitmapLock<ColorBgra32> srcLock = srcBitmap.Lock(new RectInt32(0, 0, srcBitmap.Size));
        RegionPtr<ColorBgra32> src = srcLock.AsRegionPtr();

        using IBitmapLock<ColorBgra32> outLock = output.LockBgra32();
        RegionPtr<ColorBgra32> dst = outLock.AsRegionPtr();

        InpaintPatch? patch = null;
        if (token is { RunEnabled: true } && token.HasMask && token.MaskWidth == w && token.MaskHeight == h)
        {
            patch = GetPatch(token, src, w, h);
        }

        // The output lock is addressed relative to output.Bounds (API-CHECK: if your build exposes absolute
        // coordinates instead, drop the "- bounds.Left / - bounds.Top" below).
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            if (IsCancelRequested)
            {
                return;
            }

            int oy = y - bounds.Top;
            bool rowInPatch = patch != null && y >= patch.Y && y < patch.Y + patch.Height;

            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                if (rowInPatch && x >= patch!.X && x < patch.X + patch.Width)
                {
                    int pi = ((y - patch.Y) * patch.Width + (x - patch.X)) * 4;
                    dst[x - bounds.Left, oy] = MakeBgra(patch.Bgra[pi], patch.Bgra[pi + 1], patch.Bgra[pi + 2], patch.Bgra[pi + 3]);
                }
                else
                {
                    dst[x - bounds.Left, oy] = src[x, y];
                }
            }
        }
    }

    // API-CHECK: if ColorBgra32 has a different factory name/signature in your Paint.NET build, change this one line.
    private static ColorBgra32 MakeBgra(byte b, byte g, byte r, byte a) => ColorBgra32.FromBgra(b, g, r, a);

    // ---------------------------------------------------------------------------------------------

    private InpaintPatch? GetPatch(ObjectRemoverToken t, RegionPtr<ColorBgra32> src, int w, int h)
    {
        string runKey = t.RunKey();
        string key = runKey + "|" + HashSource(src, w, h, t);

        if (ResultCache.TryGet(key, out CacheEntry hit))
        {
            return hit.Patch;
        }

        lock (ResultCache.ComputeGate)
        {
            if (ResultCache.TryGet(key, out hit))
            {
                return hit.Patch;
            }

            try
            {
                byte[] bgra = CopySource(src, w, h);
                var options = new EngineOptions(t.Model, t.Processor, t.GpuAdapter, t.ExpandPx, t.FeatherPx);
                InpaintPatch patch = InpaintEngine.Run(
                    bgra, w, h, t.Mask!, t.BoundsX0, t.BoundsY0, t.BoundsX1, t.BoundsY1, options, CancellationToken);
                ResultCache.Store(key, runKey, patch, null);
                return patch;
            }
            catch (OperationCanceledException)
            {
                return null; // preview superseded; nothing cached
            }
            catch (Exception ex)
            {
                InpaintEngine.Status = "Error: " + ex.Message;
                ResultCache.Store(key, runKey, null, ex.Message);
                return null;
            }
        }
    }

    private static byte[] CopySource(RegionPtr<ColorBgra32> src, int w, int h)
    {
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                ColorBgra32 p = src[x, y];
                int i = row + x * 4;
                bgra[i] = p.B;
                bgra[i + 1] = p.G;
                bgra[i + 2] = p.R;
                bgra[i + 3] = p.A;
            }
        }

        return bgra;
    }

    // Cheap fingerprint so a stale cached result is never reused on a different image
    // (e.g. when the effect is repeated with Ctrl+F after the user edited the picture).
    private static ulong HashSource(RegionPtr<ColorBgra32> src, int w, int h, ObjectRemoverToken t)
    {
        ulong hash = 1469598103934665603UL ^ (ulong)((long)w * 31 + h);

        int sx = Math.Max(1, w / 48), sy = Math.Max(1, h / 48);
        for (int y = 0; y < h; y += sy)
        {
            for (int x = 0; x < w; x += sx)
            {
                Fold(ref hash, src[x, y]);
            }
        }

        int x0 = Math.Max(0, t.BoundsX0 - 400), y0 = Math.Max(0, t.BoundsY0 - 400);
        int x1 = Math.Min(w, t.BoundsX1 + 400), y1 = Math.Min(h, t.BoundsY1 + 400);
        for (int y = y0; y < y1; y += 7)
        {
            for (int x = x0; x < x1; x += 7)
            {
                Fold(ref hash, src[x, y]);
            }
        }

        return hash;
    }

    private static void Fold(ref ulong hash, ColorBgra32 p)
    {
        uint v = p.B | ((uint)p.G << 8) | ((uint)p.R << 16) | ((uint)p.A << 24);
        hash = (hash ^ v) * 1099511628211UL;
    }

    private static Image CreateIcon()
    {
        var bmp = new Bitmap(16, 16);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var back = new SolidBrush(Color.FromArgb(70, 120, 200));
        g.FillEllipse(back, 1, 1, 13, 13);
        using var pen = new Pen(Color.White, 2f);
        g.DrawLine(pen, 5, 5, 10, 10);
        g.DrawLine(pen, 10, 5, 5, 10);
        return bmp;
    }
}
