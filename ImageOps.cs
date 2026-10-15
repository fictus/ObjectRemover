namespace ObjectRemoverPlugin;

/// <summary>Small, dependency-free image helpers used by the inpainting engine.</summary>
internal static class ImageOps
{
    private readonly record struct Contrib(int Start, float[] Weights);

    // Triangle (bilinear) filter whose support grows when downscaling, so shrinking a big
    // crop to 512x512 is properly anti-aliased instead of aliasing like plain bilinear.
    private static Contrib[] BuildContribs(int srcSize, int dstSize)
    {
        double scale = (double)srcSize / dstSize;
        double filterScale = Math.Max(1.0, scale);
        double support = filterScale;
        var result = new Contrib[dstSize];

        for (int i = 0; i < dstSize; i++)
        {
            double center = (i + 0.5) * scale;
            int lo = Math.Max(0, (int)(center - support + 0.5));
            int hi = Math.Min(srcSize, (int)(center + support + 0.5));
            if (hi <= lo)
            {
                lo = Math.Clamp((int)center, 0, srcSize - 1);
                hi = lo + 1;
            }

            var weights = new float[hi - lo];
            double sum = 0;
            for (int k = 0; k < weights.Length; k++)
            {
                double x = (lo + k + 0.5 - center) / filterScale;
                double v = Math.Max(0.0, 1.0 - Math.Abs(x));
                weights[k] = (float)v;
                sum += v;
            }

            if (sum <= 0)
            {
                weights[weights.Length / 2] = 1f;
                sum = 1;
            }

            for (int k = 0; k < weights.Length; k++)
            {
                weights[k] = (float)(weights[k] / sum);
            }

            result[i] = new Contrib(lo, weights);
        }

        return result;
    }

    /// <summary>Resizes an interleaved float image (ch channels) with a separable triangle filter.</summary>
    public static float[] Resize(float[] src, int sw, int sh, int ch, int dw, int dh)
    {
        if (sw == dw && sh == dh)
        {
            return (float[])src.Clone();
        }

        Contrib[] cx = BuildContribs(sw, dw);
        Contrib[] cy = BuildContribs(sh, dh);

        var tmp = new float[sh * dw * ch];
        Parallel.For(0, sh, y =>
        {
            int rowSrc = y * sw * ch;
            int rowDst = y * dw * ch;
            for (int x = 0; x < dw; x++)
            {
                Contrib c = cx[x];
                for (int k = 0; k < ch; k++)
                {
                    float acc = 0;
                    for (int i = 0; i < c.Weights.Length; i++)
                    {
                        acc += src[rowSrc + (c.Start + i) * ch + k] * c.Weights[i];
                    }

                    tmp[rowDst + x * ch + k] = acc;
                }
            }
        });

        var dst = new float[dh * dw * ch];
        int rowLen = dw * ch;
        Parallel.For(0, dh, y =>
        {
            Contrib c = cy[y];
            int rowDst = y * rowLen;
            for (int i = 0; i < c.Weights.Length; i++)
            {
                float wgt = c.Weights[i];
                int rowSrc = (c.Start + i) * rowLen;
                for (int j = 0; j < rowLen; j++)
                {
                    dst[rowDst + j] += tmp[rowSrc + j] * wgt;
                }
            }
        });

        return dst;
    }

    /// <summary>Grows a 0/255 mask by a disc of radius r pixels (only boundary pixels are stamped).</summary>
    public static byte[] DilateDisc(byte[] mask, int w, int h, int r)
    {
        var res = (byte[])mask.Clone();
        if (r <= 0)
        {
            return res;
        }

        int r2 = r * r;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (mask[i] == 0)
                {
                    continue;
                }

                bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1 ||
                            mask[i - 1] == 0 || mask[i + 1] == 0 || mask[i - w] == 0 || mask[i + w] == 0;
                if (!edge)
                {
                    continue;
                }

                for (int dy = -r; dy <= r; dy++)
                {
                    int yy = y + dy;
                    if ((uint)yy >= (uint)h)
                    {
                        continue;
                    }

                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (dx * dx + dy * dy > r2)
                        {
                            continue;
                        }

                        int xx = x + dx;
                        if ((uint)xx >= (uint)w)
                        {
                            continue;
                        }

                        res[yy * w + xx] = 255;
                    }
                }
            }
        }

        return res;
    }

    /// <summary>In-place separable box blur (edge-clamped), repeated 'passes' times (2 passes ~ gaussian).</summary>
    public static void BoxBlur(float[] a, int w, int h, int r, int passes)
    {
        if (r <= 0 || passes <= 0)
        {
            return;
        }

        var tmp = new float[a.Length];
        for (int p = 0; p < passes; p++)
        {
            BlurHorizontal(a, tmp, w, h, r);
            BlurVertical(tmp, a, w, h, r);
        }
    }

    private static void BlurHorizontal(float[] src, float[] dst, int w, int h, int r)
    {
        float inv = 1f / (2 * r + 1);
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            float acc = 0;
            for (int k = -r; k <= r; k++)
            {
                acc += src[row + Math.Clamp(k, 0, w - 1)];
            }

            for (int x = 0; x < w; x++)
            {
                dst[row + x] = acc * inv;
                int add = Math.Min(w - 1, x + r + 1);
                int sub = Math.Max(0, x - r);
                acc += src[row + add] - src[row + sub];
            }
        });
    }

    private static void BlurVertical(float[] src, float[] dst, int w, int h, int r)
    {
        float inv = 1f / (2 * r + 1);
        Parallel.For(0, w, x =>
        {
            float acc = 0;
            for (int k = -r; k <= r; k++)
            {
                acc += src[Math.Clamp(k, 0, h - 1) * w + x];
            }

            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = acc * inv;
                int add = Math.Min(h - 1, y + r + 1);
                int sub = Math.Max(0, y - r);
                acc += src[add * w + x] - src[sub * w + x];
            }
        });
    }
}
