using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.ComponentModel;

namespace ObjectRemoverPlugin;

/// <summary>
/// Image viewer with a highlight brush. Left button paints, right button erases (or toggle Eraser).
/// Mouse wheel zooms, Ctrl+wheel (or [ and ]) changes the brush size, middle button / Space+drag pans,
/// Shift+wheel / Alt+wheel scroll horizontally / vertically, scrollbars appear when the image is larger
/// than the view. Ctrl+Z undoes the last stroke.
/// </summary>
internal sealed unsafe class MaskCanvas : Control
{
    private const int TileSize = 64;
    private const int OverlayArgb = unchecked((int)0x8CFF2828); // translucent red

    private Bitmap? baseImage;      // not owned
    private Bitmap? resultImage;    // not owned
    private Bitmap? overlay;        // owned: mask drawn in red
    private byte[]? mask;
    private int iw, ih;

    private float zoom = 1f;
    private PointF pan;
    private bool autoFit = true;
    private bool painting, panning, spaceDown, hover;
    private bool strokeErase;
    private Point lastMouse;
    private PointF lastImagePoint;
    private Point cursorPos;
    private int brushSize = 40;
    private bool showResult;

    private readonly HScrollBar hBar = new() { TabStop = false, Visible = false };
    private readonly VScrollBar vBar = new() { TabStop = false, Visible = false };
    private bool syncingBars;

    private Dictionary<long, byte[]>? stroke;
    private readonly List<Dictionary<long, byte[]>> undo = new();

    public event EventHandler? MaskChanged;
    public event EventHandler? BrushSizeChanged;
    public event EventHandler? PaintStarted;

    public MaskCanvas()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = true;
        SetStyle(ControlStyles.Selectable | ControlStyles.UserMouse, true);
        BackColor = Color.FromArgb(48, 48, 48);
        Cursor = Cursors.Cross;

        hBar.Scroll += (_, _) => OnBarScroll();
        vBar.Scroll += (_, _) => OnBarScroll();
        hBar.ValueChanged += (_, _) => OnBarScroll();
        vBar.ValueChanged += (_, _) => OnBarScroll();
        Controls.Add(hBar);
        Controls.Add(vBar);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool EraseMode { get; set; }

    public int ImageWidth => iw;
    public int ImageHeight => ih;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int BrushSize
    {
        get => brushSize;
        set
        {
            int v = Math.Clamp(value, 2, 400);
            if (v == brushSize)
            {
                return;
            }

            brushSize = v;
            BrushSizeChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowResult
    {
        get => showResult;
        set
        {
            showResult = value;
            Invalidate();
        }
    }

    public bool CanUndo => undo.Count > 0;

    // ---------------------------------------------------------------------------------------------
    // Content
    // ---------------------------------------------------------------------------------------------

    public void SetImage(Bitmap image)
    {
        baseImage = image;
        iw = image.Width;
        ih = image.Height;
        mask = new byte[iw * ih];
        overlay?.Dispose();
        overlay = new Bitmap(iw, ih, PixelFormat.Format32bppArgb);
        undo.Clear();
        resultImage = null;
        autoFit = true;
        Fit();
    }

    /// <summary>Forget the image, mask, overlay, undo history and any preview result.</summary>
    public void ClearAll()
    {
        showResult = false;
        resultImage = null;
        baseImage = null;
        mask = null;
        undo.Clear();
        overlay?.Dispose();
        overlay = null;
        hBar.Visible = vBar.Visible = false;
        Invalidate();
    }

    public void SetResultImage(Bitmap? image)
    {
        resultImage = image;
        Invalidate();
    }

    public void LoadMask(byte[] source)
    {
        if (mask == null || source.Length != mask.Length)
        {
            return;
        }

        Buffer.BlockCopy(source, 0, mask, 0, source.Length);
        RebuildOverlay();
        undo.Clear();
        Invalidate();
    }

    public byte[] GetMaskCopy() => mask == null ? Array.Empty<byte>() : (byte[])mask.Clone();

    /// <summary>Bounding box of painted pixels: [x0,x1) x [y0,y1). Returns false when the mask is empty.</summary>
    public bool GetMaskBounds(out int x0, out int y0, out int x1, out int y1)
    {
        x0 = y0 = int.MaxValue;
        x1 = y1 = int.MinValue;
        if (mask == null)
        {
            return false;
        }

        for (int y = 0; y < ih; y++)
        {
            int row = y * iw;
            for (int x = 0; x < iw; x++)
            {
                if (mask[row + x] == 0)
                {
                    continue;
                }

                if (x < x0) x0 = x;
                if (x >= x1) x1 = x + 1;
                if (y < y0) y0 = y;
                y1 = y + 1;
            }
        }

        return x1 > x0;
    }

    private void RebuildOverlay()
    {
        if (overlay == null || mask == null)
        {
            return;
        }

        BitmapData bd = overlay.LockBits(new Rectangle(0, 0, iw, ih), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < ih; y++)
            {
                int* row = (int*)((byte*)bd.Scan0 + y * bd.Stride);
                for (int x = 0; x < iw; x++)
                {
                    row[x] = mask[y * iw + x] != 0 ? OverlayArgb : 0;
                }
            }
        }
        finally
        {
            overlay.UnlockBits(bd);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Undo / clear
    // ---------------------------------------------------------------------------------------------

    public void Undo()
    {
        if (mask == null || overlay == null || undo.Count == 0)
        {
            return;
        }

        Dictionary<long, byte[]> s = undo[^1];
        undo.RemoveAt(undo.Count - 1);

        foreach (KeyValuePair<long, byte[]> kv in s)
        {
            int tx = (int)(kv.Key & 0xFFFFFFFF), ty = (int)(kv.Key >> 32);
            int x0 = tx * TileSize, y0 = ty * TileSize;
            int tw = Math.Min(TileSize, iw - x0), th = Math.Min(TileSize, ih - y0);

            BitmapData bd = overlay.LockBits(new Rectangle(x0, y0, tw, th), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < th; y++)
                {
                    Buffer.BlockCopy(kv.Value, y * tw, mask, (y0 + y) * iw + x0, tw);
                    int* row = (int*)((byte*)bd.Scan0 + y * bd.Stride);
                    for (int x = 0; x < tw; x++)
                    {
                        row[x] = kv.Value[y * tw + x] != 0 ? OverlayArgb : 0;
                    }
                }
            }
            finally
            {
                overlay.UnlockBits(bd);
            }
        }

        Invalidate();
        MaskChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearMask()
    {
        if (mask == null || overlay == null)
        {
            return;
        }

        stroke = new Dictionary<long, byte[]>();
        for (int ty = 0; ty * TileSize < ih; ty++)
        {
            for (int tx = 0; tx * TileSize < iw; tx++)
            {
                int x0 = tx * TileSize, y0 = ty * TileSize;
                int tw = Math.Min(TileSize, iw - x0), th = Math.Min(TileSize, ih - y0);
                bool any = false;
                for (int y = 0; y < th && !any; y++)
                {
                    for (int x = 0; x < tw; x++)
                    {
                        if (mask[(y0 + y) * iw + x0 + x] != 0)
                        {
                            any = true;
                            break;
                        }
                    }
                }

                if (any)
                {
                    SaveTile(tx, ty);
                }
            }
        }

        bool changed = stroke.Count > 0;
        if (changed)
        {
            Array.Clear(mask);
            RebuildOverlay();
            PushUndo();
        }

        stroke = null;
        Invalidate();
        if (changed)
        {
            MaskChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SaveTile(int tx, int ty)
    {
        long key = ((long)ty << 32) | (uint)tx;
        if (stroke!.ContainsKey(key))
        {
            return;
        }

        int x0 = tx * TileSize, y0 = ty * TileSize;
        int tw = Math.Min(TileSize, iw - x0), th = Math.Min(TileSize, ih - y0);
        var data = new byte[tw * th];
        for (int y = 0; y < th; y++)
        {
            Buffer.BlockCopy(mask!, (y0 + y) * iw + x0, data, y * tw, tw);
        }

        stroke[key] = data;
    }

    private void PushUndo()
    {
        if (stroke == null || stroke.Count == 0)
        {
            return;
        }

        undo.Add(stroke);
        if (undo.Count > 40)
        {
            undo.RemoveAt(0);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Painting
    // ---------------------------------------------------------------------------------------------

    private void StampSegment(PointF p0, PointF p1)
    {
        if (mask == null || overlay == null)
        {
            return;
        }

        float r = Math.Max(0.5f, brushSize / 2f);
        var lockRect = Rectangle.Intersect(
            new Rectangle(
                (int)Math.Floor(Math.Min(p0.X, p1.X) - r - 1),
                (int)Math.Floor(Math.Min(p0.Y, p1.Y) - r - 1),
                (int)Math.Ceiling(Math.Abs(p1.X - p0.X) + 2 * r + 3),
                (int)Math.Ceiling(Math.Abs(p1.Y - p0.Y) + 2 * r + 3)),
            new Rectangle(0, 0, iw, ih));
        if (lockRect.IsEmpty)
        {
            return;
        }

        bool add = !strokeErase;
        byte value = add ? (byte)255 : (byte)0;
        int argb = add ? OverlayArgb : 0;
        float r2 = r * r;

        float dist = MathF.Sqrt((p1.X - p0.X) * (p1.X - p0.X) + (p1.Y - p0.Y) * (p1.Y - p0.Y));
        float step = Math.Max(1f, r / 3f);
        int steps = Math.Max(1, (int)Math.Ceiling(dist / step));

        BitmapData bd = overlay.LockBits(lockRect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                float cx = p0.X + (p1.X - p0.X) * t;
                float cy = p0.Y + (p1.Y - p0.Y) * t;

                int xa = Math.Max(lockRect.Left, (int)Math.Floor(cx - r));
                int xb = Math.Min(lockRect.Right - 1, (int)Math.Ceiling(cx + r));
                int ya = Math.Max(lockRect.Top, (int)Math.Floor(cy - r));
                int yb = Math.Min(lockRect.Bottom - 1, (int)Math.Ceiling(cy + r));

                for (int y = ya; y <= yb; y++)
                {
                    float dy = y + 0.5f - cy;
                    int* row = (int*)((byte*)bd.Scan0 + (y - lockRect.Top) * bd.Stride);
                    for (int x = xa; x <= xb; x++)
                    {
                        float dx = x + 0.5f - cx;
                        if (dx * dx + dy * dy > r2)
                        {
                            continue;
                        }

                        int idx = y * iw + x;
                        if (mask[idx] == value)
                        {
                            continue;
                        }

                        SaveTile(x / TileSize, y / TileSize);
                        mask[idx] = value;
                        row[x - lockRect.Left] = argb;
                    }
                }
            }
        }
        finally
        {
            overlay.UnlockBits(bd);
        }

        Invalidate(ImageRectToScreen(lockRect));
    }

    // ---------------------------------------------------------------------------------------------
    // View transform
    // ---------------------------------------------------------------------------------------------

    public void Fit()
    {
        if (baseImage == null || Width < 10 || Height < 10)
        {
            return;
        }

        // Fit against the full control size (no scrollbars are needed once the image fits)
        zoom = Math.Min((Width - 8f) / iw, (Height - 8f) / ih);
        zoom = Math.Clamp(zoom, 0.02f, 40f);
        pan = new PointF((Width - iw * zoom) / 2f, (Height - ih * zoom) / 2f);
        autoFit = true;
        ClampPan();
        Invalidate();
    }

    // ---------------------------------------------------------------------------------------------
    // Scrolling
    // ---------------------------------------------------------------------------------------------

    /// <summary>Size of the area the image is drawn in (the control minus any visible scrollbars).</summary>
    private Size ViewSize => new(
        Math.Max(1, Width - (vBar.Visible ? vBar.Width : 0)),
        Math.Max(1, Height - (hBar.Visible ? hBar.Height : 0)));

    /// <summary>
    /// Keeps the image reachable: if it is larger than the view it may be scrolled until half a
    /// viewport of empty space shows beyond its edge; otherwise it is centred. Then updates the scrollbars.
    /// </summary>
    private void ClampPan()
    {
        if (baseImage == null)
        {
            hBar.Visible = vBar.Visible = false;
            return;
        }

        // Scrollbar visibility affects the view size, which affects visibility: settle in two passes.
        for (int pass = 0; pass < 2; pass++)
        {
            Size vs = ViewSize;
            bool needH = iw * zoom > vs.Width + 0.5f;
            bool needV = ih * zoom > vs.Height + 0.5f;
            if (needH != hBar.Visible || needV != vBar.Visible)
            {
                hBar.Visible = needH;
                vBar.Visible = needV;
            }
            else
            {
                break;
            }
        }

        Size v = ViewSize;
        pan = new PointF(ClampAxis(pan.X, iw * zoom, v.Width), ClampAxis(pan.Y, ih * zoom, v.Height));
        LayoutBars();
        SyncBars();
    }

    private static float ClampAxis(float p, float content, int view)
    {
        if (content <= view)
        {
            return (view - content) / 2f;
        }

        float margin = view / 2f;
        return Math.Clamp(p, view - content - margin, margin);
    }

    private void LayoutBars()
    {
        int sw = SystemInformation.VerticalScrollBarWidth;
        int sh = SystemInformation.HorizontalScrollBarHeight;
        vBar.SetBounds(Width - sw, 0, sw, Math.Max(1, Height - (hBar.Visible ? sh : 0)));
        hBar.SetBounds(0, Height - sh, Math.Max(1, Width - (vBar.Visible ? sw : 0)), sh);
    }

    private void SyncBars()
    {
        Size vs = ViewSize;
        syncingBars = true;
        try
        {
            ConfigureBar(hBar, iw * zoom, vs.Width, pan.X);
            ConfigureBar(vBar, ih * zoom, vs.Height, pan.Y);
        }
        finally
        {
            syncingBars = false;
        }
    }

    private static void ConfigureBar(ScrollBar bar, float content, int view, float pan)
    {
        if (!bar.Visible)
        {
            return;
        }

        // Scroll position = how far the view's top/left edge is from the image's top/left edge (screen px)
        int margin = view / 2;
        int lo = -margin;
        int hi = (int)Math.Ceiling(content) + margin;
        bar.Minimum = lo;
        bar.LargeChange = Math.Max(1, view);
        bar.SmallChange = Math.Max(1, view / 10);
        bar.Maximum = hi;
        int max = Math.Max(lo, hi - bar.LargeChange + 1);
        bar.Value = Math.Clamp((int)Math.Round(-pan), lo, max);
    }

    private void OnBarScroll()
    {
        if (syncingBars || baseImage == null)
        {
            return;
        }

        pan = new PointF(hBar.Visible ? -hBar.Value : pan.X, vBar.Visible ? -vBar.Value : pan.Y);
        autoFit = false;
        Invalidate();
    }

    private PointF ToImage(Point p) => new((p.X - pan.X) / zoom, (p.Y - pan.Y) / zoom);

    private Rectangle ImageRectToScreen(Rectangle r)
    {
        int x0 = (int)Math.Floor(pan.X + r.Left * zoom) - 2;
        int y0 = (int)Math.Floor(pan.Y + r.Top * zoom) - 2;
        int x1 = (int)Math.Ceiling(pan.X + r.Right * zoom) + 2;
        int y1 = (int)Math.Ceiling(pan.Y + r.Bottom * zoom) + 2;
        return Rectangle.FromLTRB(x0, y0, x1, y1);
    }

    private Rectangle BrushScreenRect(Point c)
    {
        int rad = (int)Math.Ceiling(brushSize * zoom / 2f) + 3;
        return new Rectangle(c.X - rad, c.Y - rad, rad * 2, rad * 2);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (autoFit)
        {
            Fit();
        }
        else
        {
            ClampPan();
            Invalidate();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Input
    // ---------------------------------------------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (mask == null)
        {
            return;
        }

        if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && spaceDown))
        {
            panning = true;
            lastMouse = e.Location;
            Cursor = Cursors.SizeAll;
            return;
        }

        if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
        {
            PaintStarted?.Invoke(this, EventArgs.Empty);
            painting = true;
            strokeErase = EraseMode ^ (e.Button == MouseButtons.Right);
            stroke = new Dictionary<long, byte[]>();
            lastImagePoint = ToImage(e.Location);
            Capture = true;
            StampSegment(lastImagePoint, lastImagePoint);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        Rectangle oldBrush = BrushScreenRect(cursorPos);
        cursorPos = e.Location;
        hover = true;

        if (panning)
        {
            pan = new PointF(pan.X + e.X - lastMouse.X, pan.Y + e.Y - lastMouse.Y);
            lastMouse = e.Location;
            autoFit = false;
            ClampPan();
            Invalidate();
            return;
        }

        if (painting)
        {
            PointF p = ToImage(e.Location);
            StampSegment(lastImagePoint, p);
            lastImagePoint = p;
        }

        Invalidate(oldBrush);
        Invalidate(BrushScreenRect(cursorPos));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (panning)
        {
            panning = false;
            Cursor = Cursors.Cross;
        }

        if (painting)
        {
            painting = false;
            Capture = false;
            bool changed = stroke != null && stroke.Count > 0;
            PushUndo();
            stroke = null;
            if (changed)
            {
                MaskChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = false;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        Focus();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (baseImage == null)
        {
            return;
        }

        if ((ModifierKeys & Keys.Control) != 0)
        {
            int delta = Math.Max(2, brushSize / 10);
            BrushSize += e.Delta > 0 ? delta : -delta;
            return;
        }

        if ((ModifierKeys & (Keys.Shift | Keys.Alt)) != 0)
        {
            // Shift+wheel scrolls horizontally, Alt+wheel vertically
            float step = e.Delta / 120f * 60f;
            pan = (ModifierKeys & Keys.Shift) != 0 ? new PointF(pan.X + step, pan.Y) : new PointF(pan.X, pan.Y + step);
            autoFit = false;
            ClampPan();
            Invalidate();
            return;
        }

        float f = e.Delta > 0 ? 1.2f : 1f / 1.2f;
        float newZoom = Math.Clamp(zoom * f, 0.02f, 40f);
        f = newZoom / zoom;
        pan = new PointF(e.X - (e.X - pan.X) * f, e.Y - (e.Y - pan.Y) * f);
        zoom = newZoom;
        autoFit = false;
        ClampPan();
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space)
        {
            spaceDown = true;
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.OemOpenBrackets)
        {
            BrushSize -= Math.Max(2, brushSize / 10);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.OemCloseBrackets)
        {
            BrushSize += Math.Max(2, brushSize / 10);
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode == Keys.Z)
        {
            Undo();
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space)
        {
            spaceDown = false;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Painting the control
    // ---------------------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        if (baseImage == null || overlay == null)
        {
            return;
        }

        var dest = new RectangleF(pan.X, pan.Y, iw * zoom, ih * zoom);
        g.InterpolationMode = zoom < 1f ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        bool viewingResult = showResult && resultImage != null;
        g.DrawImage(viewingResult ? resultImage! : baseImage, dest);
        if (!viewingResult)
        {
            g.DrawImage(overlay, dest);
        }

        if (hover && !panning)
        {
            float rad = brushSize * zoom / 2f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var dark = new Pen(Color.FromArgb(200, 0, 0, 0), 3f);
            using var light = new Pen(Color.White, 1f);
            var rc = new RectangleF(cursorPos.X - rad, cursorPos.Y - rad, rad * 2, rad * 2);
            g.DrawEllipse(dark, rc);
            g.DrawEllipse(light, rc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            overlay?.Dispose();
            overlay = null;
        }

        base.Dispose(disposing);
    }
}
