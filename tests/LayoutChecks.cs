using CodexUsageMonitor;

internal static class LayoutChecks
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var fiveHour = new UsageWindow(0, 300, DateTimeOffset.Now.AddHours(5));
        var weekly = new UsageWindow(31, 10080, DateTimeOffset.Now.AddDays(6));
        var plus = new UsageSnapshot(fiveHour, weekly, 2, "plus", DateTimeOffset.Now);
        var pro = new UsageSnapshot(null, weekly, null, "pro", DateTimeOffset.Now);
        if (plus.IndicatorWindow != fiveHour) throw new Exception("The shortest window should drive the tray icon.");
        if (pro.IndicatorWindow != weekly) throw new Exception("The weekly window should drive the Pro tray icon.");
        CheckPanel(plus, "plus-card.png");
        CheckPanel(pro, "pro-card.png");
    }

    private static void CheckPanel(UsageSnapshot snapshot, string fileName)
    {
        using var panel = new UsagePanel();
        panel.UpdateUsage(snapshot, null, false);
        panel.SetUpdate(new ReleaseUpdate("9.0.0", UpdateChecker.Repository + "/releases/download/v9.0.0/app.zip"));
        Console.WriteLine($"Card: {panel.ClientRectangle}");
        foreach (Control c in panel.Controls) Console.WriteLine($"{c.AccessibleName}: {c.Bounds}");
        foreach (Control control in panel.Controls)
        {
            if (!panel.ClientRectangle.Contains(control.Bounds)) throw new Exception($"Control exceeds card: {control.AccessibleName}");
            foreach (Control other in panel.Controls)
                if (control != other && control.Bounds.IntersectsWith(other.Bounds)) throw new Exception($"Controls overlap: {control.Text} / {other.Text}");
        }
        using var image = new Bitmap(panel.Width, panel.Height);
        panel.DrawToBitmap(image, panel.ClientRectangle);
        foreach (Control control in panel.Controls) control.DrawToBitmap(image, control.Bounds);
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        image.Save(path);
        Console.WriteLine($"Layout checks passed. Preview: {path}");
    }
}
