using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexUsageMonitor;

internal sealed record ReleaseUpdate(string Version, string DownloadUrl);
internal enum UpdateCheckResult { Skipped, Busy, Available, UpToDate, NoRelease, Failed }

internal sealed class UpdateState
{
    public bool Automatic { get; set; } = true;
    public DateTimeOffset? LastAttempt { get; set; }
    public ReleaseUpdate? Available { get; set; }
}

internal sealed class UpdateChecker : IDisposable
{
    internal const string Repository = "https://github.com/whiskeylover/codex-usage-monitor";
    internal static Version CurrentVersion => typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0);
    private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codex-usage-monitor", "updates.json");
    private readonly HttpClient client;
    private bool busy;
    private readonly bool useRegistry;
    private const string PreferenceKey = @"Software\whiskeylover\CodexUsageMonitor";
    private int generation;
    public UpdateState State { get; private set; } = new();
    public event Action? Changed;

    public UpdateChecker(HttpMessageHandler? handler = null, string? statePath = null)
    {
        if (statePath is not null) path = statePath;
        useRegistry = statePath is null && OperatingSystem.IsWindows();
        client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexUsageMonitor/" + CurrentVersion.ToString(3));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        try { State = JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        if (useRegistry && OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PreferenceKey);
                if (key?.GetValue("AutomaticUpdates") is int value)
                {
                    State.Automatic = value != 0;
                    Save(); // Keep the JSON preference consistent with Setup's choice.
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }

    public void SetAutomatic(bool enabled)
    {
        State.Automatic = enabled;
        if (useRegistry && OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(PreferenceKey);
                key.SetValue("AutomaticUpdates", enabled ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        generation++;
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(State));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public async Task CheckIfDueAsync(CancellationToken cancellationToken)
    {
        await CheckAsync(false, cancellationToken);
    }

    public Task<UpdateCheckResult> CheckNowAsync(CancellationToken cancellationToken) => CheckAsync(true, cancellationToken);

    private async Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken)
    {
        if (busy) return UpdateCheckResult.Busy;
        if (cancellationToken.IsCancellationRequested || !manual && (!State.Automatic || State.LastAttempt is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromDays(1))) return UpdateCheckResult.Skipped;
        busy = true;
        var requestGeneration = generation;
        State.LastAttempt = DateTimeOffset.UtcNow;
        Save(); // Rate-limit attempts across restarts, including failed requests.
        try
        {
            using var response = await client.GetAsync("https://api.github.com/repos/whiskeylover/codex-usage-monitor/releases/latest", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                if (generation == requestGeneration) State.Available = null;
                return UpdateCheckResult.NoRelease;
            }
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var update = ParseRelease(document.RootElement, CurrentVersion);
            if (generation == requestGeneration) State.Available = update;
            return update is null ? UpdateCheckResult.UpToDate : UpdateCheckResult.Available;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return UpdateCheckResult.Failed;
        }
        finally
        {
            busy = false;
            Save();
            if (!cancellationToken.IsCancellationRequested) Changed?.Invoke();
        }
    }

    internal static ReleaseUpdate? ParseRelease(JsonElement release, Version current)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || Normalize(version) <= Normalize(current)) return null;
        // Prefer Setup; retain compatibility with older ZIP-only releases.
        foreach (var extension in new[] { "-setup.exe", ".zip" })
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != $"codex-usage-monitor-v{version.ToString(3)}-win-x64{extension}") continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            if (IsDownloadUrl(url)) return new ReleaseUpdate(version.ToString(3), url!);
        }
        return null;
    }

    internal static bool IsDownloadUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == "github.com" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        uri.AbsolutePath.StartsWith("/whiskeylover/codex-usage-monitor/releases/download/", StringComparison.Ordinal);
    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    public ReleaseUpdate? Available => State.Available is { } update && IsDownloadUrl(update.DownloadUrl) &&
        Version.TryParse(update.Version, out var version) && Normalize(version) > Normalize(CurrentVersion) ? update : null;
    public void Dispose() => client.Dispose();
}
