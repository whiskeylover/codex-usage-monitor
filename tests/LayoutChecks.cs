using CodexUsageMonitor;

internal static class LayoutChecks
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var panel = new UsagePanel();
        panel.UpdateUsage(new UsageSnapshot(new UsageWindow(0, 300, DateTimeOffset.Now.AddHours(5)),
            new UsageWindow(31, 10080, DateTimeOffset.Now.AddDays(6)), 2, "plus", DateTimeOffset.Now), null, false);
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
        var path = Path.Combine(AppContext.BaseDirectory, "unified-card.png");
        image.Save(path);
        Console.WriteLine($"Layout checks passed. Preview: {path}");
    }
}
