using System.Diagnostics;
using System.Net.Http;

namespace ObjectRemoverPlugin;

/// <summary>Modal progress window for downloading the model files. Cancel / close keeps the partial file for resuming.</summary>
internal sealed class ModelDownloadDialog : Form
{
    private readonly IReadOnlyList<ModelInfo> models;
    private readonly Label headline = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft };
    private readonly ProgressBar bar = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000, Style = ProgressBarStyle.Marquee };
    private readonly Label detail = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopLeft };
    private readonly Button cancelBtn = new() { Text = "Cancel", Width = 90, Height = 28, Anchor = AnchorStyles.Right };
    private readonly CancellationTokenSource cts = new();
    private readonly Stopwatch clock = new();
    private bool finished;
    private int speedIndex = -1;
    private long speedBaseBytes;
    private long speedBaseMs;

    public string? Error { get; private set; }

    public ModelDownloadDialog(IReadOnlyList<ModelInfo> models, Color back, Color fore)
    {
        this.models = models;

        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Downloading AI models";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(470, 150);
        BackColor = back;
        ForeColor = fore;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(14, 10, 14, 10) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.Controls.Add(headline, 0, 0);
        layout.Controls.Add(bar, 0, 1);
        layout.Controls.Add(detail, 0, 2);
        layout.Controls.Add(cancelBtn, 0, 3);
        Controls.Add(layout);

        headline.Text = "Starting download...";
        cancelBtn.Click += (_, _) =>
        {
            if (finished)
            {
                Close();
            }
            else
            {
                cts.Cancel();
                cancelBtn.Enabled = false;
                headline.Text = "Stopping... (your progress is kept)";
            }
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        clock.Start();
        _ = RunAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!finished)
        {
            // Closing with the X while a download runs = cancel; the form closes itself once the download task has stopped
            e.Cancel = true;
            cts.Cancel();
            cancelBtn.Enabled = false;
            headline.Text = "Stopping... (your progress is kept)";
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            cts.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task RunAsync()
    {
        var progress = new UiProgress(p => OnUi(() => Report(p)));
        try
        {
            await ModelStore.DownloadAsync(models, progress, cts.Token).ConfigureAwait(false);
            OnUi(() =>
            {
                finished = true;
                DialogResult = DialogResult.OK;
            });
        }
        catch (OperationCanceledException)
        {
            OnUi(() =>
            {
                finished = true;
                DialogResult = DialogResult.Cancel;
            });
        }
        catch (Exception ex)
        {
            OnUi(() =>
            {
                finished = true;
                Error = Describe(ex);
                bar.Style = ProgressBarStyle.Continuous;
                headline.Text = "The download was interrupted.";
                detail.Text = Error + "\r\nYour progress is saved. Click OK in the plugin again to resume.";
                cancelBtn.Text = "Close";
                cancelBtn.Enabled = true;
            });
        }
    }

    private void OnUi(Action a)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            try { BeginInvoke(a); } catch (InvalidOperationException) { }
        }
        else
        {
            a();
        }
    }

    private void Report(DownloadProgress p)
    {
        if (finished)
        {
            return;
        }

        headline.Text = $"Downloading {p.Name}  ({p.FileIndex + 1} of {p.FileCount})";
        if (p.Total <= 0)
        {
            bar.Style = ProgressBarStyle.Marquee;
            detail.Text = FormatBytes(p.Done);
            return;
        }

        bar.Style = ProgressBarStyle.Continuous;
        bar.Value = (int)Math.Clamp(p.Done * 1000 / p.Total, 0, 1000);

        // Speed is measured from where this file's download started in this session, so resumed bytes don't inflate it
        long ms = clock.ElapsedMilliseconds;
        if (speedIndex != p.FileIndex)
        {
            speedIndex = p.FileIndex;
            speedBaseBytes = p.Done;
            speedBaseMs = ms;
        }

        string speed = "";
        if (ms - speedBaseMs > 1000)
        {
            double bps = (p.Done - speedBaseBytes) * 1000.0 / (ms - speedBaseMs);
            speed = $"   {FormatBytes((long)bps)}/s";
        }

        detail.Text = $"{FormatBytes(p.Done)} of {FormatBytes(p.Total)}  ({p.Done * 100 / p.Total}%){speed}";
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException h when h.StatusCode != null => $"The server answered with an error ({(int)h.StatusCode}).",
        HttpRequestException => "Couldn't reach the download server. Check your internet connection.",
        System.IO.IOException io => io.Message,
        UnauthorizedAccessException => "Couldn't write to " + ModelStore.SharedDir + ".",
        _ => ex.Message,
    };

    private static string FormatBytes(long b) => b >= 1L << 20 ? $"{b / 1048576.0:0.0} MB" : $"{b / 1024.0:0} KB";

    private sealed class UiProgress : IProgress<DownloadProgress>
    {
        private readonly Action<DownloadProgress> handler;

        public UiProgress(Action<DownloadProgress> handler) => this.handler = handler;

        public void Report(DownloadProgress value) => handler(value);
    }
}
