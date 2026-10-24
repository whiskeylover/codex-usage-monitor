using System.Net;
using System.Text.Json;
using CodexUsageMonitor;

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static string Release(string tag, bool prerelease = false, string? url = null) => JsonSerializer.Serialize(new
{
    tag_name = tag, draft = false, prerelease,
    assets = new[] { new { name = $"codex-usage-monitor-{tag}-win-x64.zip", browser_download_url = url ?? UpdateChecker.Repository + $"/releases/download/{tag}/app.zip" } }
});
static ReleaseUpdate? Parse(string json, string current = "0.3.2")
{
    using var doc = JsonDocument.Parse(json);
    return UpdateChecker.ParseRelease(doc.RootElement, Version.Parse(current));
}
Require(Parse(Release("v0.3.10")) is not null, "Versions must compare numerically.");
var setupRelease = JsonSerializer.Serialize(new
{
    tag_name = "v0.4.0", draft = false, prerelease = false,
    assets = new[]
    {
        new { name = "codex-usage-monitor-v0.4.0-win-x64.zip", browser_download_url = UpdateChecker.Repository + "/releases/download/v0.4.0/app.zip" },
        new { name = "codex-usage-monitor-v0.4.0-win-x64-setup.exe", browser_download_url = UpdateChecker.Repository + "/releases/download/v0.4.0/setup.exe" }
    }
});
Require(Parse(setupRelease)?.DownloadUrl.EndsWith("/setup.exe") == true, "Setup must be preferred over a ZIP regardless of asset order.");
Require(Parse(Release("v0.3.2"), "0.3.2.0") is null, "Equivalent versions must not trigger an update.");
Require(Parse(Release("v0.3.1")) is null, "Older releases must be ignored.");
Require(Parse(Release("v0.4.0", true)) is null, "Prereleases must be ignored.");
Require(Parse(Release("v0.4.0", url: "https://example.com/app.zip")) is null, "Off-repository downloads must be rejected.");
Require(!UpdateChecker.IsDownloadUrl("https://github.com.evil.test/whiskeylover/codex-usage-monitor/releases/download/v1/a.zip"), "Lookalike hosts must be rejected.");
using (var doc = JsonDocument.Parse(Release("v0.4.0").Replace("win-x64.zip", "source.zip")))
    Require(UpdateChecker.ParseRelease(doc.RootElement, new Version(0, 3, 2)) is null, "Source-only releases must not offer a binary download.");

var folder = Path.Combine(Path.GetTempPath(), "codex-update-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var path = Path.Combine(folder, "updates.json");
    var handler = new FakeHandler(HttpStatusCode.OK, Release("v9.0.0"));
    using (var checker = new UpdateChecker(handler, path))
    {
        checker.SetAutomatic(false);
        await checker.CheckIfDueAsync(default);
        Require(handler.Count == 0, "Disabled checks must make no request.");
        checker.SetAutomatic(true);
        await checker.CheckIfDueAsync(default);
        await checker.CheckIfDueAsync(default);
        Require(handler.Count == 1 && checker.Available?.Version == "9.0.0", "Cache must prevent duplicate daily checks.");
        checker.SetAutomatic(false);
        var result = await checker.CheckNowAsync(default);
        Require(handler.Count == 2 && result == UpdateCheckResult.Available, "Manual checks must bypass the daily cache and automatic opt-out.");
        Require(!checker.State.Automatic, "Manual checks must not enable automatic checking.");
        result = await checker.CheckNowAsync(default);
        Require(handler.Count == 3 && result == UpdateCheckResult.Available, "Each deliberate manual check must request fresh release metadata.");
        checker.SetAutomatic(true);
    }
    var cachedHandler = new FakeHandler(HttpStatusCode.OK, "{}");
    using (var checker = new UpdateChecker(cachedHandler, path))
    {
        await checker.CheckIfDueAsync(default);
        Require(cachedHandler.Count == 0 && checker.Available is not null, "Cached result and rate limit must survive restart.");
        checker.State.LastAttempt = DateTimeOffset.UtcNow.AddDays(-2);
        await checker.CheckIfDueAsync(default); // malformed response must remain silent
        Require(cachedHandler.Count == 1, "Checks must resume after 24 hours.");
    }
    var failureHandler = new FakeHandler(HttpStatusCode.ServiceUnavailable, "");
    using (var checker = new UpdateChecker(failureHandler, Path.Combine(folder, "failure.json")))
    {
        await checker.CheckIfDueAsync(default);
        await checker.CheckIfDueAsync(default);
        Require(failureHandler.Count == 1, "Failures must not create a retry loop.");
        Require(await checker.CheckNowAsync(default) == UpdateCheckResult.Failed, "Manual failures must be distinguishable from being up to date.");
    }
    var missingHandler = new FakeHandler(HttpStatusCode.NotFound, "");
    using (var checker = new UpdateChecker(missingHandler, Path.Combine(folder, "missing.json")))
    {
        await checker.CheckIfDueAsync(default);
        Require(checker.Available is null, "No published release must be handled silently.");
    }
    Console.WriteLine("Passed: version ordering, stable binary selection, URL validation, opt-out, daily cache, restart persistence, malformed responses, failures, and missing releases.");
}
finally { Directory.Delete(folder, true); }

sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public int Count { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Count++;
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
