using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PaintDotNet;
using PaintDotNet.Effects;
using PaintDotNet.Imaging;
using PaintDotNet.Rendering;

namespace ObjectRemoverPlugin;

internal sealed unsafe class ObjectRemoverConfigDialog : EffectConfigForm<ObjectRemoverEffect, ObjectRemoverToken>
{
    private readonly MaskCanvas canvas = new() { Dock = DockStyle.Fill };

    private readonly TrackBar brushTrack = new() { Minimum = 2, Maximum = 400, TickStyle = TickStyle.None, AutoSize = false };
    private readonly NumericUpDown brushNum = new() { Minimum = 2, Maximum = 400, Value = 40 };
    private readonly CheckBox eraseCheck = new() { Text = "Eraser (right-click also erases)", AutoSize = true };
    private readonly Button undoBtn = new() { Text = "Undo", AutoSize = true };
    private readonly Button clearBtn = new() { Text = "Clear mask", AutoSize = true };
    private readonly Button fitBtn = new() { Text = "Fit view", AutoSize = true };

    private readonly ComboBox modelBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox procBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown adapterNum = new() { Minimum = 0, Maximum = 15, Value = 0 };
    private readonly NumericUpDown expandNum = new() { Minimum = 0, Maximum = 64, Value = 6 };
    private readonly NumericUpDown featherNum = new() { Minimum = 0, Maximum = 32, Value = 4 };
    private readonly CheckBox autoCheck = new() { Text = "Preview automatically after each stroke", AutoSize = true };
    private readonly CheckBox showResultCheck = new() { Text = "Show result (uncheck to see the mask)", AutoSize = true };
    private readonly Button previewBtn = new() { Text = "Preview removal", Height = 32, Dock = DockStyle.Top };
    private readonly Label statusLabel = new() { AutoSize = false, Dock = DockStyle.Fill };
    private readonly Button okBtn = new() { Text = "OK", Width = 90, Height = 30 };
    private readonly Button cancelBtn = new() { Text = "Cancel", Width = 90, Height = 30 };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };

    private Bitmap? baseBmp;
    private Bitmap? resultBmp;
    private int iw, ih;
    private bool initialized, updating, maskLoaded;

    private byte[]? maskSnapshot;
    private int bx0, by0, bx1, by1;
    private Guid revision = Guid.NewGuid();
    private bool runEnabled;
    private int shownVersion;
    private string lastStatus = "";

    public ObjectRemoverConfigDialog()
    {
        Text = "AI Object Remover";
        UseAppThemeColors = true;
        ClientSize = new Size(S(1080), S(720));
        MinimumSize = new Size(S(860), S(600));
        BuildUi();
        ApplyTheme(this);
        WireEvents();
        timer.Start();
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    // ---------------------------------------------------------------------------------------------
    // UI
    // ---------------------------------------------------------------------------------------------

    private void BuildUi()
    {
        modelBox.Items.AddRange(new object[]
        {
            "LaMa - best quality (recommended)",
            "MI-GAN - fast, works well on GPU",
        });
        modelBox.SelectedIndex = 0;

        procBox.Items.AddRange(new object[]
        {
            "Auto (GPU if it works, else CPU)",
            "GPU (DirectML)",
            "CPU only",
        });
        procBox.SelectedIndex = 0;

        brushTrack.Value = 40;
        brushTrack.Dock = DockStyle.Fill;
        brushTrack.Height = S(26);

        var side = new Panel { Dock = DockStyle.Right, Width = S(330), Padding = new Padding(S(8)) };

        // --- bottom buttons
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = S(46),
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, S(6), 0, 0),
        };
        bottom.Controls.Add(cancelBtn);
        bottom.Controls.Add(okBtn);
        AcceptButton = okBtn;
        CancelButton = cancelBtn;

        // --- status
        statusLabel.Text = "Paint over the object you want to remove, then press \"Preview removal\" (or just OK).";
        var statusBox = new GroupBox { Text = "Status", Dock = DockStyle.Bottom, Height = S(96) };
        statusBox.Controls.Add(statusLabel);
        statusLabel.Padding = new Padding(S(4));

        // --- brush group
        var brushTable = NewTable(2);
        AddRow(brushTable, "Brush size (px)", brushNum);
        AddWide(brushTable, brushTrack);
        AddWide(brushTable, eraseCheck);
        var brushButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        brushButtons.Controls.AddRange(new Control[] { undoBtn, clearBtn, fitBtn });
        AddWide(brushTable, brushButtons);
        AddWide(brushTable, new Label
        {
            Text = "Wheel: zoom   Ctrl+wheel or [ ]: brush size\nMiddle-drag or Space+drag: pan   Ctrl+Z: undo",
            AutoSize = true,
            MaximumSize = new Size(S(270), 0),
            ForeColor = SystemColors.GrayText,
        });
        var brushGroup = NewGroup("Highlight brush", brushTable);

        // --- removal group
        var optTable = NewTable(2);
        AddRow(optTable, "Model", modelBox);
        AddRow(optTable, "Processor", procBox);
        AddRow(optTable, "GPU adapter #", adapterNum);
        AddRow(optTable, "Grow mask (px)", expandNum);
        AddRow(optTable, "Edge blend (px)", featherNum);
        AddWide(optTable, autoCheck);
        AddWide(optTable, showResultCheck);
        AddWide(optTable, previewBtn);
        var optGroup = NewGroup("Removal", optTable);

        var flow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true,
        };
        flow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        flow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        flow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        flow.Controls.Add(brushGroup, 0, 0);
        flow.Controls.Add(optGroup, 0, 1);

        side.Controls.Add(flow);
        side.Controls.Add(statusBox);
        side.Controls.Add(bottom);

        Controls.Add(canvas);
        Controls.Add(side);
    }

    private TableLayoutPanel NewTable(int cols)
    {
        var t = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = cols,
            Dock = DockStyle.Fill,
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    private GroupBox NewGroup(string title, Control inner)
    {
        var g = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(S(280), 0),
            Margin = new Padding(0, 0, 0, S(8)),
            Padding = new Padding(S(6)),
        };
        inner.Dock = DockStyle.Top;
        g.Controls.Add(inner);
        return g;
    }

    private static void AddRow(TableLayoutPanel t, string label, Control c)
    {
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 3) });
        c.Margin = new Padding(3);
        c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        t.Controls.Add(c);
    }

    private static void AddWide(TableLayoutPanel t, Control c)
    {
        c.Margin = new Padding(3);
        t.Controls.Add(c);
        t.SetColumnSpan(c, t.ColumnCount);
        c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
    }

    private void ApplyTheme(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            if (c == canvas)
            {
                continue;
            }

            if (c is Button or ComboBox or NumericUpDown or TextBoxBase)
            {
                c.BackColor = BackColor;
                c.ForeColor = ForeColor;
                if (c is Button b)
                {
                    b.UseVisualStyleBackColor = false;
                    b.FlatStyle = FlatStyle.Flat;
                }
                else if (c is ComboBox cb)
                {
                    cb.FlatStyle = FlatStyle.Flat;
                }
            }
            else if (c is not TrackBar)
            {
                c.ForeColor = c.ForeColor == SystemColors.GrayText ? c.ForeColor : ForeColor;
            }

            if (c.HasChildren)
            {
                ApplyTheme(c);
            }
        }
    }

    private void WireEvents()
    {
        canvas.MaskChanged += (_, _) => OnMaskEdited();
        canvas.PaintStarted += (_, _) =>
        {
            if (showResultCheck.Checked)
            {
                showResultCheck.Checked = false;
            }
        };
        canvas.BrushSizeChanged += (_, _) => SyncBrush(canvas.BrushSize);

        brushTrack.ValueChanged += (_, _) => { if (!updating) { canvas.BrushSize = brushTrack.Value; } };
        brushNum.ValueChanged += (_, _) => { if (!updating) { canvas.BrushSize = (int)brushNum.Value; } };
        eraseCheck.CheckedChanged += (_, _) => canvas.EraseMode = eraseCheck.Checked;
        undoBtn.Click += (_, _) => canvas.Undo();
        clearBtn.Click += (_, _) => canvas.ClearMask();
        fitBtn.Click += (_, _) => canvas.Fit();

        modelBox.SelectedIndexChanged += (_, _) => OnSettingChanged();
        procBox.SelectedIndexChanged += (_, _) => { adapterNum.Enabled = procBox.SelectedIndex != 2; OnSettingChanged(); };
        adapterNum.ValueChanged += (_, _) => OnSettingChanged();
        expandNum.ValueChanged += (_, _) => OnSettingChanged();
        featherNum.ValueChanged += (_, _) => OnSettingChanged();
        showResultCheck.CheckedChanged += (_, _) => canvas.ShowResult = showResultCheck.Checked && resultBmp != null;

        previewBtn.Click += (_, _) => RequestRun();
        okBtn.Click += (_, _) =>
        {
            runEnabled = HasMask;
            PushToken();
            DialogResult = DialogResult.OK;
            Close();
        };
        cancelBtn.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        timer.Tick += (_, _) => PollEngine();
    }

    // ---------------------------------------------------------------------------------------------
    // Behaviour
    // ---------------------------------------------------------------------------------------------

    private bool HasMask => maskSnapshot != null && bx1 > bx0 && by1 > by0;

    private void SyncBrush(int size)
    {
        updating = true;
        try
        {
            brushTrack.Value = Math.Clamp(size, brushTrack.Minimum, brushTrack.Maximum);
            brushNum.Value = Math.Clamp(size, (int)brushNum.Minimum, (int)brushNum.Maximum);
        }
        finally
        {
            updating = false;
        }
    }

    private void OnMaskEdited()
    {
        if (!initialized)
        {
            return;
        }

        maskSnapshot = canvas.GetMaskCopy();
        if (!canvas.GetMaskBounds(out bx0, out by0, out bx1, out by1))
        {
            bx0 = by0 = bx1 = by1 = 0;
        }

        revision = Guid.NewGuid();
        InvalidateResult();
        runEnabled = autoCheck.Checked && HasMask;
        PushToken();
    }

    private void OnSettingChanged()
    {
        if (updating || !initialized)
        {
            return;
        }

        InvalidateResult();
        runEnabled = autoCheck.Checked && HasMask;
        PushToken();
    }

    private void RequestRun()
    {
        if (!HasMask)
        {
            statusLabel.Text = "Nothing to remove yet - paint over an object first.";
            return;
        }

        if (!System.IO.File.Exists(InpaintEngine.ModelPath(SelectedModel)))
        {
            statusLabel.Text = $"Model file missing: {InpaintEngine.ModelPath(SelectedModel)}";
            return;
        }

        runEnabled = true;
        InpaintEngine.Status = "Working...";
        statusLabel.Text = "Working... (the first run loads the model and can take a while)";
        PushToken();
    }

    private void PushToken() => UpdateTokenFromDialog();

    private void InvalidateResult()
    {
        showResultCheck.Checked = false;
        canvas.ShowResult = false;
    }

    private ModelKind SelectedModel => (ModelKind)Math.Max(0, modelBox.SelectedIndex);

    private string CurrentRunKey() => ObjectRemoverToken.MakeRunKey(
        revision, SelectedModel, (ProcessorMode)Math.Max(0, procBox.SelectedIndex),
        (int)adapterNum.Value, (int)expandNum.Value, (int)featherNum.Value);

    private void PollEngine()
    {
        string st = InpaintEngine.Status;
        if (st != lastStatus)
        {
            lastStatus = st;
            statusLabel.Text = st;
        }

        CacheEntry? e = ResultCache.Latest;
        if (e != null && e.Version != shownVersion && e.RunKey == CurrentRunKey())
        {
            shownVersion = e.Version;
            if (e.Patch != null)
            {
                ShowPatch(e.Patch);
            }
            else if (e.Error != null)
            {
                statusLabel.Text = "Error: " + e.Error;
            }
        }
    }

    private void ShowPatch(InpaintPatch p)
    {
        if (baseBmp == null)
        {
            return;
        }

        resultBmp?.Dispose();
        resultBmp = new Bitmap(baseBmp);
        BitmapData bd = resultBmp.LockBits(new Rectangle(p.X, p.Y, p.Width, p.Height), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            for (int y = 0; y < p.Height; y++)
            {
                Marshal.Copy(p.Bgra, y * p.Width * 4, bd.Scan0 + y * bd.Stride, p.Width * 4);
            }
        }
        finally
        {
            resultBmp.UnlockBits(bd);
        }

        canvas.SetResultImage(resultBmp);
        showResultCheck.Checked = true;
        canvas.ShowResult = true;
    }

    // ---------------------------------------------------------------------------------------------
    // Token plumbing (Paint.NET calls these)
    // ---------------------------------------------------------------------------------------------

    protected override ObjectRemoverToken OnCreateInitialToken() => new ObjectRemoverToken();

    protected override void OnUpdateDialogFromToken(ObjectRemoverToken t)
    {
        updating = true;
        try
        {
            EnsureSourceLoaded();

            modelBox.SelectedIndex = Math.Clamp((int)t.Model, 0, modelBox.Items.Count - 1);
            procBox.SelectedIndex = Math.Clamp((int)t.Processor, 0, procBox.Items.Count - 1);
            adapterNum.Value = Math.Clamp(t.GpuAdapter, (int)adapterNum.Minimum, (int)adapterNum.Maximum);
            adapterNum.Enabled = procBox.SelectedIndex != 2;
            expandNum.Value = Math.Clamp(t.ExpandPx, (int)expandNum.Minimum, (int)expandNum.Maximum);
            featherNum.Value = Math.Clamp(t.FeatherPx, (int)featherNum.Minimum, (int)featherNum.Maximum);
            canvas.BrushSize = t.BrushSize;
            SyncBrush(canvas.BrushSize);

            // Repeat-effect case: restore the previous mask if it fits this image
            if (!maskLoaded && t.Mask != null && t.MaskWidth == iw && t.MaskHeight == ih)
            {
                canvas.LoadMask(t.Mask);
                maskSnapshot = t.Mask;
                bx0 = t.BoundsX0; by0 = t.BoundsY0; bx1 = t.BoundsX1; by1 = t.BoundsY1;
                revision = t.MaskRevision == Guid.Empty ? Guid.NewGuid() : t.MaskRevision;
            }

            maskLoaded = true;
        }
        finally
        {
            updating = false;
            initialized = true;
        }

        string modelPath = InpaintEngine.ModelPath(SelectedModel);
        if (!System.IO.File.Exists(modelPath))
        {
            statusLabel.Text = $"Model file missing: {modelPath}";
        }
    }

    protected override void OnUpdateTokenFromDialog(ObjectRemoverToken t)
    {
        t.Mask = maskSnapshot;
        t.MaskWidth = iw;
        t.MaskHeight = ih;
        t.BoundsX0 = bx0; t.BoundsY0 = by0; t.BoundsX1 = bx1; t.BoundsY1 = by1;
        t.MaskRevision = revision;
        t.RunEnabled = runEnabled && HasMask;
        t.Model = SelectedModel;
        t.Processor = (ProcessorMode)Math.Max(0, procBox.SelectedIndex);
        t.GpuAdapter = (int)adapterNum.Value;
        t.ExpandPx = (int)expandNum.Value;
        t.FeatherPx = (int)featherNum.Value;
        t.BrushSize = canvas.BrushSize;
    }

    private void EnsureSourceLoaded()
    {
        if (baseBmp != null)
        {
            return;
        }

        using IEffectInputBitmap<ColorBgra32> source = Environment.GetSourceBitmapBgra32();
        iw = source.Size.Width;
        ih = source.Size.Height;
        baseBmp = new Bitmap(iw, ih, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

        using IBitmapLock<ColorBgra32> srcLock = source.Lock(new RectInt32(0, 0, source.Size));
        RegionPtr<ColorBgra32> region = srcLock.AsRegionPtr();

        BitmapData bd = baseBmp.LockBits(new Rectangle(0, 0, iw, ih), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < ih; y++)
            {
                byte* row = (byte*)bd.Scan0 + y * bd.Stride;
                for (int x = 0; x < iw; x++)
                {
                    ColorBgra32 p = region[x, y];
                    row[x * 4] = p.B;
                    row[x * 4 + 1] = p.G;
                    row[x * 4 + 2] = p.R;
                    row[x * 4 + 3] = p.A;
                }
            }
        }
        finally
        {
            baseBmp.UnlockBits(bd);
        }

        canvas.SetImage(baseBmp);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop();
        timer.Dispose();
        canvas.SetResultImage(null);
        resultBmp?.Dispose();
        resultBmp = null;
        base.OnFormClosed(e);
    }
}
