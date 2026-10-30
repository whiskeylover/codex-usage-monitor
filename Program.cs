using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace CodexUsageMonitor;

internal record UsageWindow(double Used, int Minutes, DateTimeOffset Reset);
internal record UsageSnapshot(UsageWindow? FiveHour, UsageWindow? Weekly, int? FreeResets, string? Plan, DateTimeOffset CheckedAt)
{
    public UsageWindow? IndicatorWindow => new[] { FiveHour, Weekly }
        .Where(window => window is not null)
        .OrderBy(window => window!.Minutes)
        .FirstOrDefault();
}

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
            await SendAsync(process, new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "codex-usage-monitor", title = "Codex Usage Monitor", version = UpdateChecker.CurrentVersion.ToString(3) } } }, cancellationToken);
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

internal sealed class UsagePanel : Form
{
    private UsageSnapshot? snapshot;
    private string? error;
    private bool loading;
    private string? updateStatus;
    private bool shuttingDown;
    private float shutdownProgress;
    private readonly ToolTip tips = new();
    private readonly Button refresh;
    private readonly LinkLabel updateLink;
    public event Action? QuitRequested;
    public bool IsPreview { get; private set; }
    protected override bool ShowWithoutActivation => IsPreview;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            if (IsPreview) parameters.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return parameters;
        }
    }
    public event Action? RefreshRequested;
    public UsagePanel()
    {
        Text = "Codex usage";
        TopMost = true;
        KeyPreview = true;
        KeyDown += (_, e) => { if (!shuttingDown && e.KeyCode == Keys.Escape) Hide(); };

        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = new Size(346, 320);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(25, 28, 35);
        ForeColor = Color.White;
        DoubleBuffered = true;
        Padding = new Padding(18);
        Deactivate += (_, _) => { if (!IsPreview && !shuttingDown) Hide(); };
        Resize += (_, _) => PopupStyle.Round(this);
        PopupStyle.Round(this);
        refresh = AddIcon("↻", "Refresh usage", new Rectangle(262, 14, 30, 30), () => RefreshRequested?.Invoke(), Color.FromArgb(100, 220, 182));
        AddIcon("⏻", "Quit application", new Rectangle(306, 14, 28, 30), () => QuitRequested?.Invoke(), Color.FromArgb(245, 110, 110));
        AddLink($"v{UpdateChecker.CurrentVersion.ToString(3)}", new Rectangle(18, 258, 64, 22), () => CopyVersion());
        AddLink("GitHub", new Rectangle(92, 258, 56, 22), () => OpenLink(UpdateChecker.Repository));
        AddLink("Issues", new Rectangle(166, 258, 48, 22), () => OpenLink(UpdateChecker.Repository + "/issues"));
        AddLink("Releases", new Rectangle(234, 258, 80, 22), () => OpenLink(UpdateChecker.Repository + "/releases"));
        updateLink = AddLink("Update available", new Rectangle(203, 226, 130, 22), () =>
        {
            if (updateLink?.Tag is string url) OpenLink(url);
        });
        updateLink.Visible = false;
    }

    private Button AddIcon(string glyph, string label, Rectangle bounds, Action action, Color color)
    {
        var button = new Button
        {
            Text = glyph, AccessibleName = label, Bounds = bounds, FlatStyle = FlatStyle.Flat,
            BackColor = BackColor, ForeColor = color, Font = new Font("Segoe UI Symbol", 13f),
            Cursor = Cursors.Hand, TabStop = true
        };
        button.FlatAppearance.BorderSize = 0;
        tips.SetToolTip(button, label);
        button.Click += (_, _) => action();
        Controls.Add(button);
        return button;
    }

    private LinkLabel AddLink(string text, Rectangle bounds, Action action)
    {
        var link = new LinkLabel
        {
            Text = text, Bounds = bounds, Font = new Font("Segoe UI", 8.5f),
            LinkColor = Color.FromArgb(100, 220, 182), ActiveLinkColor = Color.White,
            VisitedLinkColor = Color.FromArgb(100, 220, 182), LinkBehavior = LinkBehavior.HoverUnderline,
            TextAlign = ContentAlignment.MiddleLeft, AccessibleName = text
        };
        if (text.StartsWith("v", StringComparison.Ordinal)) tips.SetToolTip(link, "Copy version information");
        link.LinkClicked += (_, _) => action();
        Controls.Add(link);
        return link;
    }

    private void CopyVersion()
    {
        try
        {
            Clipboard.SetText($"Codex Usage Monitor {UpdateChecker.CurrentVersion.ToString(3)}\r\n{System.Runtime.InteropServices.RuntimeInformation.OSDescription}\r\n.NET {Environment.Version}");
            tips.Show("Version information copied", this, 18, 248, 1600);
        }
        catch (System.Runtime.InteropServices.ExternalException) { tips.Show("Clipboard busy. Try again.", this, 18, 267, 1600); }
    }

    private static void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Diagnostics.Log($"Could not open link: {ex.Message}"); }
    }

    public void SetUpdate(ReleaseUpdate? update)
    {
        updateLink.Visible = update is not null && !shuttingDown;
        updateLink.Tag = update?.DownloadUrl;
        tips.SetToolTip(updateLink, update is null ? "" : $"Download version {update.Version} from GitHub");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] out bool value, uint flags);

    public async Task ShowShutdownAsync()
    {
        shuttingDown = true;
        ShowAtTray();
        foreach (Control control in Controls) { control.Enabled = false; control.Visible = false; }
        var duration = SystemParametersInfo(0x1042, 0, out var animationsEnabled, 0) && animationsEnabled ? 450 : 0;
        var clock = Stopwatch.StartNew();
        do
        {
            shutdownProgress = duration == 0 ? 1 : Math.Clamp((float)clock.Elapsed.TotalMilliseconds / duration, 0, 1);
            Invalidate();
            if (duration == 0) break;
            await Task.Delay(16);
        } while (clock.ElapsedMilliseconds < duration);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tips.Dispose();
        base.Dispose(disposing);
    }

    public void UpdateUsage(UsageSnapshot? s, string? failure, bool busy)
    {
        updateStatus = null;
        snapshot = s; error = failure; loading = busy; refresh.Enabled = !busy && !shuttingDown; Invalidate();
    }
    public void ShowUpdateStatus(string status)
    {
        updateStatus = status;
        Invalidate();
    }
    public void ShowAtTray(bool preview = false)
    {
        if (!Visible)
        {
            IsPreview = preview;
            if (IsHandleCreated) UpdateStyles();
            PopupStyle.Position(this);
            Show();
        }
        if (!preview)
        {
            IsPreview = false;
            UpdateStyles();
            // Tray windows belong to Explorer; Activate alone may leave it
            // in the foreground, preventing a later Deactivate notification.
            SetForegroundWindow(Handle);
            Activate();
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    protected override void WndProc(ref Message message)
    {
        // Promote a preview only on an actual mouse interaction. An initial
        // Windows activation notification must not change hover mode.
        if (message.Msg == 0x0021 && IsPreview) // WM_MOUSEACTIVATE
        {
            IsPreview = false;
            UpdateStyles();
        }
        base.WndProc(ref message);
    }

    public void DismissIfFocusLost()
    {
        // Also covers activation refused by Windows: no initial focus is
        // required for this fallback to close a click-opened card.
        if (Visible && !IsPreview && !shuttingDown && GetForegroundWindow() != Handle) Hide();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

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
        if (shuttingDown)
        {
            g.DrawString("Shutting down…", title, white, 65, Height / 2 - 20);
            using var track = new SolidBrush(Color.FromArgb(61, 68, 81));
            g.FillRectangle(track, 65, Height / 2 + 24, Width - 130, 3);
            g.FillRectangle(dim, 65, 190, (Width - 130) * (1 - shutdownProgress), 3);
            return;
        }
        using var heading = new Font("Segoe UI", 11.5f, FontStyle.Bold);
        g.DrawString("Codex Usage Monitor", heading, white, 18, 20);
        g.DrawString("By whiskeylover · MIT License", small, dim, 18, 44);
        var windowTop = 77;
        if (snapshot?.FiveHour is { } fiveHour)
        {
            DrawWindow(g, "5-hour allowance", fiveHour, windowTop);
            windowTop += 66;
        }
        if (snapshot?.Weekly is { } weekly)
            DrawWindow(g, "Weekly allowance", weekly, windowTop);
        if (snapshot is { FiveHour: null, Weekly: null } && error is null)
            g.DrawString("No allowance windows reported", normal, dim, 18, windowTop);
        g.DrawLine(Pens.DimGray, 18, 250, Width - 18, 250);
        g.DrawString("Unofficial · Not affiliated with OpenAI", small, dim, 18, 290);
        if (updateStatus is not null)
        {
            g.DrawString(updateStatus, small, dim, new RectangleF(18, 205, Width - 36, 20));
            return;
        }
        if (snapshot is not null && error is null)
        {
            var credits = snapshot.FreeResets is int n ? $" · {n} free resets" : "";
            var plan = snapshot.Plan is string x ? x.ToUpperInvariant() : "Codex";
            g.DrawString($"{plan}{credits}", small, dim, 18, 207);
            g.DrawString($"Updated {snapshot.CheckedAt:HH:mm:ss}", small, dim, 18, 229);
        }
        else if (loading) g.DrawString("Checking Codex usage…", normal, dim, 18, 248);
        if (error is not null)
        {
            using var red = new SolidBrush(Color.FromArgb(255, 155, 155));
            var message = error.Length > 65 ? error[..65] + "…" : error;
            g.DrawString(message, small, red, new RectangleF(18, 205, Width - 36, 40));
        }
        else if (loading && snapshot is not null && !updateLink.Visible) g.DrawString("Refreshing…", small, dim, 225, 229);
    }
    private void DrawWindow(Graphics g, string name, UsageWindow window, int top)
    {
        using var labelFont = new Font("Segoe UI", 10, FontStyle.Bold);
        using var detailFont = new Font("Segoe UI", 8.5f);
        using var white = new SolidBrush(Color.White);
        using var muted = new SolidBrush(Color.FromArgb(165, 174, 185));
        g.DrawString(name, labelFont, white, 18, top);
        var remaining = Math.Clamp(100 - window.Used, 0, 100);
        var value = $"{remaining:0.#}% left";
        var size = g.MeasureString(value, detailFont);
        g.DrawString(value, detailFont, white, Width - 18 - size.Width, top + 3);
        var track = new Rectangle(18, top + 24, Width - 36, 8);
        using var bg = new SolidBrush(Color.FromArgb(61, 68, 81));
        using var fill = new SolidBrush(remaining <= 15 ? Color.FromArgb(245, 110, 110) : Color.FromArgb(61, 198, 155));
        g.FillRectangle(bg, track);
        g.FillRectangle(fill, track.X, track.Y, (int)(track.Width * remaining / 100), track.Height);
        var reset = window.Reset.ToLocalTime();
        var left = reset - DateTimeOffset.Now;
        var timeRemaining = Math.Clamp(left.TotalMinutes / window.Minutes, 0, 1);
        var timeTrack = new Rectangle(track.X, track.Bottom, track.Width, 5);
        using var timeFill = new SolidBrush(Color.FromArgb(105, 164, 213));
        g.FillRectangle(bg, timeTrack);
        g.FillRectangle(timeFill, timeTrack.X, timeTrack.Y, (int)(timeTrack.Width * timeRemaining), timeTrack.Height);
        string countdown = left.TotalSeconds <= 0 ? "Reset due" :
            left.TotalDays >= 1 ? $"{(int)left.TotalDays}d {left.Hours}h" :
            left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m" :
            $"{Math.Max(0, (int)left.TotalMinutes)}m";
        g.DrawString($"Time left: {countdown} · resets {reset:ddd h:mm tt}", detailFont, muted, 18, top + 42);
    }
}

internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly UsagePanel panel;
    private readonly UpdateChecker updates = new();
    private readonly System.Windows.Forms.Timer updateSchedule;
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

        hoverDelay = new System.Windows.Forms.Timer { Interval = 650 };
        hoverDelay.Tick += (_, _) =>
        {
            hoverDelay.Stop();
            if (!exiting && mouseOverTray && !panel.Visible &&
                Distance(Cursor.Position, hoverPoint) <= 16)
                panel.ShowAtTray(preview: true);
        };
        hoverWatch = new System.Windows.Forms.Timer { Interval = 180 };
        hoverWatch.Tick += (_, _) =>
        {
            panel.DismissIfFocusLost();
            // MouseMove stops when the pointer leaves the icon. Compare with
            // its last reported position even when the pointer then stops.
            if (mouseOverTray && Distance(Cursor.Position, hoverPoint) > 16)
            {
                mouseOverTray = false;
                hoverDelay.Stop();
            }
            if (!exiting && panel.Visible && panel.IsPreview)
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
        panel.QuitRequested += () => _ = ShutdownAsync();
        panel.SetUpdate(updates.Available);
        updates.Changed += () => { if (!exiting) panel.SetUpdate(updates.Available); };
        updateSchedule = new System.Windows.Forms.Timer { Interval = 60_000 };
        updateSchedule.Tick += (_, _) => _ = updates.CheckIfDueAsync(stopping.Token);
        updateSchedule.Start();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show usage", null, (_, _) => TogglePanel());
        menu.Items.Add("Refresh now", null, (_, _) => _ = RefreshAsync());
        menu.Items.Add("Open diagnostics", null, (_, _) => OpenDiagnostics());
        menu.Items.Add(new ToolStripSeparator());
        var checkUpdates = new ToolStripMenuItem("Check for updates");
        checkUpdates.ToolTipText = "Check GitHub now, even if automatic checks are off.";
        checkUpdates.Click += async (_, _) =>
        {
            if (exiting) return;
            checkUpdates.Enabled = false;
            TogglePanel();
            panel.ShowUpdateStatus("Checking for updates…");
            try
            {
                var result = await updates.CheckNowAsync(stopping.Token);
                if (exiting) return;
                panel.ShowUpdateStatus(result switch
                {
                    UpdateCheckResult.Available => "A new version is available.",
                    UpdateCheckResult.UpToDate => "You’re up to date.",
                    UpdateCheckResult.NoRelease => "No published release is available yet.",
                    UpdateCheckResult.Busy => "An update check is already running.",
                    _ => "Couldn’t check for updates. Try again."
                });
            }
            finally { if (!exiting) checkUpdates.Enabled = true; }
        };
        menu.Items.Add(checkUpdates);
        var automaticUpdates = new ToolStripMenuItem("Check for updates automatically")
        {
            Checked = updates.State.Automatic, CheckOnClick = true
        };
        automaticUpdates.ToolTipText = "Contact GitHub at most once daily; no account or usage data is sent.";
        automaticUpdates.CheckedChanged += (_, _) =>
        {
            updates.SetAutomatic(automaticUpdates.Checked);
            if (automaticUpdates.Checked) _ = updates.CheckIfDueAsync(stopping.Token);
        };
        menu.Items.Add(automaticUpdates);
        menu.Items.Add("Quit application", null, (_, _) => _ = ShutdownAsync());
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
            if (!exiting && !hoverDelay.Enabled && !panel.Visible &&
                !menu.Visible)
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
        initialRefresh.Tick += (_, _) => { initialRefresh.Stop(); _ = RefreshAsync(); _ = updates.CheckIfDueAsync(stopping.Token); };
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
        var indicator = snapshot?.IndicatorWindow;
        // Never reinstate a native tooltip: it competes with the formatted panel.
        tray.Text = null!;
        var old = tray.Icon;
        tray.Icon = MakeIcon(indicator is null ? null : 100 - indicator.Used);
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
    private async Task ShutdownAsync()
    {
        if (exiting) return;
        exiting = true;
        stopping.Cancel();
        polling.Stop(); countdown.Stop(); initialRefresh.Stop(); updateSchedule.Stop();
        hoverDelay.Stop(); hoverWatch.Stop();
        try { await panel.ShowShutdownAsync(); }
        finally
        {
            tray.Visible = false;
            tray.Dispose(); panel.Close(); panel.Dispose();
            hoverDelay.Dispose(); hoverWatch.Dispose(); updateSchedule.Dispose();
            polling.Dispose(); countdown.Dispose(); initialRefresh.Dispose();
            // An in-flight check is canceled and cannot update the disposed panel.
            updates.Dispose();
            ExitThread();
        }
    }

}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Match Inno Setup's AppMutex and prevent startup/finish-launch duplicates.
        using var instance = new Mutex(true, @"Local\CodexUsageMonitor", out var firstInstance);
        if (!firstInstance) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
