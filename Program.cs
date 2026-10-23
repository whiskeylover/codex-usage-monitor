using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace CodexUsageMonitor;

internal record UsageWindow(double Used, int Minutes, DateTimeOffset Reset);
internal record UsageSnapshot(UsageWindow? FiveHour, UsageWindow? Weekly, int? FreeResets, string? Plan, DateTimeOffset CheckedAt);

internal static class CodexReader
{
    public static async Task<UsageSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("codex", "app-server")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8
        };
        // npm's global codex may be a codex.cmd shim, which Process.Start cannot
        // always launch with UseShellExecute=false. Resolve it using cmd.exe on Windows.
        start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        start.Arguments = "/d /s /c \"codex app-server\"";
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Unable to launch Codex CLI.");
        try
        {
            Diagnostics.Log("Process launched; sending initialize");
            await SendAsync(process, new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "codex-usage-monitor", title = "Codex Usage Monitor", version = "0.2.1" } } }, cancellationToken);
            await AwaitResponseAsync(process, 1, cancellationToken);
            Diagnostics.Log("Initialize response received");
            await SendAsync(process, new { method = "initialized", @params = new { } }, cancellationToken);
            Diagnostics.Log("Requesting account/rateLimits/read");
            await SendAsync(process, new { id = 2, method = "account/rateLimits/read", @params = new { } }, cancellationToken);
            using var response = await AwaitResponseAsync(process, 2, cancellationToken);
            Diagnostics.Log("Rate limits response received");
            var root = response.RootElement;
            if (!root.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("rateLimits", out var limits))
                throw new InvalidOperationException("Codex did not return rate limits. Check `codex login`.");

            static UsageWindow? Window(JsonElement parent, string name)
            {
                if (!parent.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
                var used = w.GetProperty("usedPercent").GetDouble();
                var mins = w.GetProperty("windowDurationMins").GetInt32();
                var reset = DateTimeOffset.FromUnixTimeSeconds(w.GetProperty("resetsAt").GetInt64());
                return new UsageWindow(used, mins, reset);
            }
            var windows = new[] { Window(limits, "primary"), Window(limits, "secondary") }.Where(w => w is not null).ToArray();
            var five = windows.FirstOrDefault(w => w!.Minutes == 300);
            var week = windows.FirstOrDefault(w => w!.Minutes == 10080);
            int? credits = null;
            if (result.TryGetProperty("rateLimitResetCredits", out var resets) &&
                resets.ValueKind == JsonValueKind.Object &&
                resets.TryGetProperty("availableCount", out var count) && count.ValueKind == JsonValueKind.Number)
                credits = count.GetInt32();
            string? plan = null;
            if (limits.TryGetProperty("planType", out var p) && p.ValueKind == JsonValueKind.String)
                plan = p.GetString();
            return new UsageSnapshot(five, week, credits, plan, DateTimeOffset.Now);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
    }

    static async Task SendAsync(Process p, object msg, CancellationToken ct)
    {
        await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(msg).AsMemory(), ct);
        await p.StandardInput.FlushAsync(ct);
    }

    static async Task<JsonDocument> AwaitResponseAsync(Process p, int id, CancellationToken ct)
    {
        while (true)
        {
            var line = await p.StandardOutput.ReadLineAsync(ct);
            if (line is null) throw new InvalidOperationException("Codex exited before replying.");
            JsonDocument document;
            try { document = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            if (document.RootElement.TryGetProperty("id", out var responseId) &&
                responseId.ValueKind == JsonValueKind.Number && responseId.GetInt32() == id)
            {
                if (document.RootElement.TryGetProperty("error", out var error))
                {
                    var message = error.ToString();
                    document.Dispose();
                    throw new InvalidOperationException(message);
                }
                return document;
            }
            document.Dispose();
        }
    }
}

internal static class Diagnostics
{
    private static readonly object Sync = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "codex-usage-monitor", "diagnostics.log");
    public static void Log(string details)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"[{DateTimeOffset.Now:O}] {details}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

internal static class PopupStyle
{
    public static void Position(Form form)
    {
        var mouse = Cursor.Position;
        var bounds = Screen.FromPoint(mouse).WorkingArea;
        form.Location = new Point(
            Math.Clamp(mouse.X - form.Width / 2, bounds.Left + 4, Math.Max(bounds.Left + 4, bounds.Right - form.Width - 4)),
            Math.Clamp(mouse.Y - form.Height - 12, bounds.Top + 4, Math.Max(bounds.Top + 4, bounds.Bottom - form.Height - 4)));
    }
    public static void Round(Form form)
    {
        using var shape = new GraphicsPath();
        int r = 18; var w = form.Width; var h = form.Height;
        shape.AddArc(0, 0, r, r, 180, 90);
        shape.AddArc(w-r-1, 0, r, r, 270, 90);
        shape.AddArc(w-r-1, h-r-1, r, r, 0, 90);
        shape.AddArc(0, h-r-1, r, r, 90, 90);
        shape.CloseFigure();
        var old = form.Region;
        form.Region = new Region(shape);
        old?.Dispose();
    }
}

internal sealed class AboutPanel : Form
{
    private const string Repository = "https://github.com/whiskeylover/codex-usage-monitor";
    private static readonly string Version = typeof(AboutPanel).Assembly.GetName().Version?.ToString(3) ?? "0.2.1";
    private readonly Icon? appIcon;
    private readonly ToolTip feedback = new();

    public AboutPanel()
    {
        Text = "About Codex Usage Monitor";
        ClientSize = new Size(346, 238);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(25, 28, 35);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9f);
        DoubleBuffered = true;
        KeyPreview = true;
        appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Deactivate += (_, _) => Hide();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; } };
        Resize += (_, _) => PopupStyle.Round(this);
        PopupStyle.Round(this);

        AddLabel("Codex Usage Monitor", 62, 20, 260, 25, 12f, Color.White, FontStyle.Bold);
        AddLabel($"Version {Version}  ·  By whiskeylover", 62, 47, 264, 20, 8.5f, Muted);
        AddLabel("Your Codex allowances, at a glance.", 20, 86, 306, 24, 10f, Color.White);
        AddLabel("Open source · MIT License", 20, 116, 306, 20, 8.5f, Muted);
        AddLabel("Unofficial; not affiliated with OpenAI.", 20, 137, 306, 20, 8.5f, Muted);

        AddAction("GitHub", new Rectangle(16, 174, 68, 25), () => OpenLink(Repository));
        AddAction("Report an issue", new Rectangle(91, 174, 112, 25), () => OpenLink(Repository + "/issues"));
        AddAction("Release notes", new Rectangle(213, 174, 113, 25), () => OpenLink(Repository + "/releases"));
        AddAction("Copy version info", new Rectangle(16, 202, 145, 25), () =>
        {
            try
            {
                Clipboard.SetText($"Codex Usage Monitor {Version}\r\n{System.Runtime.InteropServices.RuntimeInformation.OSDescription}\r\n.NET {Environment.Version}");
                feedback.Show("Version info copied", this, 20, 199, 1800);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                feedback.Show("Clipboard busy. Try again.", this, 20, 199, 1800);
            }
        });
        AddAction("Close", new Rectangle(267, 202, 59, 25), Hide);
        AccessibleName = Text;
    }

    private static Color Muted => Color.FromArgb(167, 177, 192);

    private void AddLabel(string text, int x, int y, int width, int height, float size, Color color, FontStyle style = FontStyle.Regular)
    {
        Controls.Add(new Label
        {
            Text = text, Bounds = new Rectangle(x, y, width, height),
            Font = new Font("Segoe UI", size, style), ForeColor = color,
            UseMnemonic = false
        });
    }

    private void AddAction(string text, Rectangle bounds, Action action)
    {
        var link = new LinkLabel
        {
            Text = text, Bounds = bounds, LinkColor = Color.FromArgb(100, 220, 182),
            ActiveLinkColor = Color.White, VisitedLinkColor = Color.FromArgb(100, 220, 182),
            LinkBehavior = LinkBehavior.HoverUnderline, TextAlign = ContentAlignment.MiddleCenter,
            AccessibleName = text, TabStop = true
        };
        link.LinkClicked += (_, _) => action();
        Controls.Add(link);
    }

    private void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Diagnostics.Log($"Could not open About link: {ex.Message}"); }
    }

    public void ShowAtTray()
    {
        PopupStyle.Position(this);
        Show(); Activate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (appIcon is not null) e.Graphics.DrawIcon(appIcon, new Rectangle(20, 24, 32, 32));
        using var line = new Pen(Color.FromArgb(60, 67, 79));
        e.Graphics.DrawLine(line, 20, 166, ClientSize.Width - 20, 166);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { appIcon?.Dispose(); feedback.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class UsagePanel : Form
{
    private UsageSnapshot? snapshot;
    private string? error;
    private bool loading;
    public bool IsPreview { get; private set; }
    protected override bool ShowWithoutActivation => IsPreview;
    public event Action? RefreshRequested;
    public UsagePanel()
    {
        Text = "Codex usage";
        TopMost = true;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        Activated += (_, _) => IsPreview = false;
        Width = 346;
        Height = 305;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(25, 28, 35);
        ForeColor = Color.White;
        DoubleBuffered = true;
        Padding = new Padding(18);
        Deactivate += (_, _) => Hide();
        Resize += (_, _) => PopupStyle.Round(this);
        PopupStyle.Round(this);
        var refresh = new Button
        {
            Text = "↻ Refresh", Width = 94, Height = 30,
            Location = new Point(230, 16), FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(44, 49, 60), ForeColor = Color.White,
            Cursor = Cursors.Hand, TabStop = true
        };
        refresh.FlatAppearance.BorderSize = 0;
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        Controls.Add(refresh);
    }
    public void UpdateUsage(UsageSnapshot? s, string? failure, bool busy)
    {
        snapshot = s; error = failure; loading = busy; Invalidate();
    }
    public void ShowAtTray(bool preview = false)
    {
        if (!Visible)
        {
            IsPreview = preview;
            PopupStyle.Position(this);
            Show();
        }
        if (!preview)
        {
            IsPreview = false;
            Activate();
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var title = new Font("Segoe UI", 15, FontStyle.Bold);
        using var normal = new Font("Segoe UI", 9.5f);
        using var small = new Font("Segoe UI", 8.5f);
        using var dim = new SolidBrush(Color.FromArgb(164, 173, 187));
        using var white = new SolidBrush(Color.White);
        g.DrawString("Codex usage", title, white, 18, 17);
        DrawWindow(g, "5-hour allowance", snapshot?.FiveHour, 71);
        DrawWindow(g, "Weekly allowance", snapshot?.Weekly, 151);
        g.DrawLine(Pens.DimGray, 18, 237, Width - 18, 237);
        if (snapshot is not null)
        {
            var credits = snapshot.FreeResets is int n ? $" · {n} free resets" : "";
            var plan = snapshot.Plan is string x ? x.ToUpperInvariant() : "Codex";
            g.DrawString($"{plan}{credits}", small, dim, 18, 248);
            g.DrawString($"Updated {snapshot.CheckedAt:HH:mm:ss}", small, dim, 18, 267);
        }
        else if (loading) g.DrawString("Checking Codex usage…", normal, dim, 18, 248);
        if (error is not null)
        {
            using var red = new SolidBrush(Color.FromArgb(255, 155, 155));
            var message = error.Length > 65 ? error[..65] + "…" : error;
            g.DrawString(message, small, red, new RectangleF(18, 245, Width - 36, 45));
        }
        else if (loading && snapshot is not null) g.DrawString("Refreshing…", small, dim, 225, 267);
    }
    private void DrawWindow(Graphics g, string name, UsageWindow? window, int top)
    {
        using var labelFont = new Font("Segoe UI", 10, FontStyle.Bold);
        using var detailFont = new Font("Segoe UI", 8.5f);
        using var white = new SolidBrush(Color.White);
        using var muted = new SolidBrush(Color.FromArgb(165, 174, 185));
        g.DrawString(name, labelFont, white, 18, top);
        if (window is null)
        {
            g.DrawString("Unavailable", detailFont, muted, 235, top + 2);
            return;
        }
        var remaining = Math.Clamp(100 - window.Used, 0, 100);
        var value = $"{remaining:0.#}% left";
        var size = g.MeasureString(value, detailFont);
        g.DrawString(value, detailFont, white, Width - 18 - size.Width, top + 3);
        var track = new Rectangle(18, top + 29, Width - 36, 10);
        using var bg = new SolidBrush(Color.FromArgb(61, 68, 81));
        using var fill = new SolidBrush(remaining <= 15 ? Color.FromArgb(245, 110, 110) : Color.FromArgb(61, 198, 155));
        g.FillRectangle(bg, track);
        g.FillRectangle(fill, track.X, track.Y, (int)(track.Width * remaining / 100), track.Height);
        var reset = window.Reset.ToLocalTime();
        var left = reset - DateTimeOffset.Now;
        string countdown = left.TotalSeconds <= 0 ? "Reset due" :
            left.TotalDays >= 1 ? $"{(int)left.TotalDays}d {left.Hours}h" :
            left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m" :
            $"{Math.Max(0, (int)left.TotalMinutes)}m";
        g.DrawString($"{window.Used:0.#}% used  ·  Resets in {countdown} ({reset:ddd h:mm tt})", detailFont, muted, 18, top + 46);
    }
}

internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly UsagePanel panel;
    private readonly AboutPanel about;
    private readonly System.Windows.Forms.Timer hoverDelay;
    private readonly System.Windows.Forms.Timer hoverWatch;
    private Point hoverPoint;
    private bool mouseOverTray;
    private DateTime? previewLeaveAt;
    private readonly System.Windows.Forms.Timer polling;
    private readonly System.Windows.Forms.Timer initialRefresh;
    private readonly System.Windows.Forms.Timer countdown;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private UsageSnapshot? snapshot;
    private string? error;
    private bool working;
    private bool exiting;
    public TrayApp()
    {
        panel = new UsagePanel();
        about = new AboutPanel();
        hoverDelay = new System.Windows.Forms.Timer { Interval = 650 };
        hoverDelay.Tick += (_, _) =>
        {
            hoverDelay.Stop();
            if (mouseOverTray && !panel.Visible && !about.Visible &&
                Distance(Cursor.Position, hoverPoint) <= 16)
                panel.ShowAtTray(preview: true);
        };
        hoverWatch = new System.Windows.Forms.Timer { Interval = 180 };
        hoverWatch.Tick += (_, _) =>
        {
            // MouseMove stops when the pointer leaves the icon. Compare with
            // its last reported position even when the pointer then stops.
            if (mouseOverTray && Distance(Cursor.Position, hoverPoint) > 16)
            {
                mouseOverTray = false;
                hoverDelay.Stop();
            }
            if (panel.Visible && panel.IsPreview)
            {
                if (mouseOverTray || panel.Bounds.Contains(Cursor.Position))
                    previewLeaveAt = null;
                else if (previewLeaveAt is null)
                    previewLeaveAt = DateTime.UtcNow;
                else if ((DateTime.UtcNow - previewLeaveAt.Value).TotalMilliseconds >= 500)
                    panel.Hide();
            }
            else previewLeaveAt = null;
        };
        hoverWatch.Start();
        panel.RefreshRequested += () => _ = RefreshAsync();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show usage", null, (_, _) => TogglePanel());
        menu.Items.Add("Refresh now", null, (_, _) => _ = RefreshAsync());
        menu.Items.Add("Open diagnostics", null, (_, _) => OpenDiagnostics());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("About", null, (_, _) => ShowAbout());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        tray = new NotifyIcon
        {
            Icon = MakeIcon(null),
            // Null suppresses Windows' plain-text tooltip so only our custom
            // formatted hover panel appears (NotifyIcon.Text documentation).
            Text = null!,
            Visible = true,
            ContextMenuStrip = menu
        };
        tray.MouseMove += (_, _) =>
        {
            hoverPoint = Cursor.Position;
            mouseOverTray = true;
            // Re-arm after a click or another popup closes, even if the
            // pointer never left the icon. Do not restart an active delay.
            if (!hoverDelay.Enabled && !panel.Visible &&
                !about.Visible && !menu.Visible)
                hoverDelay.Start();
        };
        menu.Opening += (_, _) => { panel.Hide(); hoverDelay.Stop(); };
        tray.MouseClick += (_, e) =>
        {
            hoverDelay.Stop();
            if (e.Button == MouseButtons.Left) TogglePanel();
        };
        polling = new System.Windows.Forms.Timer { Interval = 120_000 };
        polling.Tick += (_, _) => _ = RefreshAsync();
        countdown = new System.Windows.Forms.Timer { Interval = 30_000 };
        countdown.Tick += (_, _) => { UpdateDisplay(); panel.Invalidate(); };
        polling.Start(); countdown.Start();
        // The application context is constructed before Application.Run enters
        // the WinForms message loop. Schedule the first request on that loop
        // so async continuations safely return to the UI thread.
        initialRefresh = new System.Windows.Forms.Timer { Interval = 500 };
        initialRefresh.Tick += (_, _) => { initialRefresh.Stop(); _ = RefreshAsync(); };
        initialRefresh.Start();
    }
    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
    private void TogglePanel()
    {
        hoverDelay.Stop();
        about.Hide();
        panel.ShowAtTray();
    }
    private async Task RefreshAsync()
    {
        if (exiting || !await gate.WaitAsync(0)) return;
        working = true; error = null; UpdateDisplay();
        try
        {
            Diagnostics.Log("Refresh started");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            snapshot = await CodexReader.ReadAsync(timeout.Token);
            Diagnostics.Log("Refresh succeeded");
        }
        catch (OperationCanceledException) { if (!exiting) { error = "Codex request timed out"; LogError(error); } }
        catch (Exception ex) { error = ex.Message; LogError(ex.ToString()); }
        finally
        {
            working = false;
            if (!exiting) UpdateDisplay();
            gate.Release();
        }
    }
    private static readonly string DiagnosticPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "codex-usage-monitor", "diagnostics.log");
    private static void LogError(string details) => Diagnostics.Log(details);
    private static void OpenDiagnostics()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticPath)!);
            if (!File.Exists(DiagnosticPath))
                File.WriteAllText(DiagnosticPath, "No refresh errors recorded yet." + Environment.NewLine);
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{DiagnosticPath}\"") { UseShellExecute = true });
        }
        catch { /* No modal error dialogs. */ }
    }
    private void UpdateDisplay()
    {
        if (exiting) return;
        var five = snapshot?.FiveHour;
        var week = snapshot?.Weekly;
        string text = five is null ? "Codex usage: unavailable" :
            $"Codex 5h: {100 - five.Used:0}% left | Week: {(week is null ? "?" : $"{100 - week.Used:0}%")} left";
        if (error is not null && snapshot is null) text = "Codex usage: refresh failed";
        // Never reinstate a native tooltip: it competes with the formatted panel.
        tray.Text = null!;
        var old = tray.Icon;
        tray.Icon = MakeIcon(five is null ? null : 100 - five.Used);
        old?.Dispose();
        panel.UpdateUsage(snapshot, error, working);
    }
    private static Icon MakeIcon(double? remaining)
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var baseBrush = new SolidBrush(Color.FromArgb(23, 28, 39));
        using var border = new Pen(Color.FromArgb(65, 75, 91), 1.2f);
        using (var rounded = new GraphicsPath())
        {
            rounded.AddArc(1, 1, 8, 8, 180, 90);
            rounded.AddArc(23, 1, 8, 8, 270, 90);
            rounded.AddArc(23, 23, 8, 8, 0, 90);
            rounded.AddArc(1, 23, 8, 8, 90, 90);
            rounded.CloseFigure();
            g.FillPath(baseBrush, rounded);
            g.DrawPath(border, rounded);
        }
        using var track = new Pen(Color.FromArgb(80, 89, 105), 3.5f);
        using var progress = new Pen(remaining is null ? Color.FromArgb(105, 164, 213) : remaining <= 15 ? Color.FromArgb(245, 110, 110) : Color.FromArgb(61, 198, 155), 3.5f);
        track.StartCap = track.EndCap = System.Drawing.Drawing2D.LineCap.Round;
        progress.StartCap = progress.EndCap = System.Drawing.Drawing2D.LineCap.Round;
        g.DrawEllipse(track, 7, 7, 18, 18);
        if (remaining is not null) g.DrawArc(progress, 7, 7, 18, 18, -90, (float)(359.5 * Math.Clamp(remaining.Value, 0, 100) / 100));
        else g.DrawArc(progress, 7, 7, 18, 18, -90, 95);
        using var center = new SolidBrush(Color.FromArgb(235, 248, 246));
        g.FillEllipse(center, 14, 14, 4, 4);
        IntPtr handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
    private void ShowAbout()
    {
        hoverDelay.Stop();
        if (about.Visible) about.Hide();
        else { panel.Hide(); about.ShowAtTray(); }
    }
    private void Shutdown()
    {
        exiting = true;
        stopping.Cancel();
        polling.Stop(); countdown.Stop(); initialRefresh.Stop();
        hoverDelay.Stop(); hoverWatch.Stop();
        tray.Visible = false;
        tray.Dispose(); panel.Close(); panel.Dispose(); about.Close(); about.Dispose();
        hoverDelay.Dispose(); hoverWatch.Dispose();
        polling.Dispose(); countdown.Dispose(); initialRefresh.Dispose();
        ExitThread();
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
