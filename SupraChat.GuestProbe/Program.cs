using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SupraChat.Core;

// A deliberately small public-safe external-service probe. No cookies, profiles,
// browser storage, text transcript, screenshots, screenshots/trace, or HTTP captures
// are persisted. A single preapproved benign guest prompt is the only page mutation.
const string schema = "suprachat-anonymous-chatgpt-probe/v1";
const string marker = "GUEST_PROBE_OK";
const string prompt = "Please reply with exactly GUEST_PROBE_OK and nothing else.";
const string target = "https://chatgpt.com/";
const int observationSeconds = 45;

string? output = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--output" && i + 1 < args.Length) output = args[++i];
    else if (args[i] == "--self-test")
    {
        if (Classify("Just a moment... Checking your browser", false) != "CHALLENGE_OR_BROWSER_CHECK" ||
            Classify("Sign up or Log in to ChatGPT", false) != "LOGIN_GATE" ||
            Classify("Ask anything", false) != "COMPOSER_NOT_FOUND" ||
            Classify("Ask anything", true) != "GUEST_COMPOSER_VISIBLE")
            throw new InvalidOperationException("guest-probe classification self-test failed");
        Console.WriteLine("guest-probe classification self-test PASS");
        return 0;
    }
    else throw new ArgumentException("Usage: [--output <path>] [--self-test]");
}

var result = new ProbeReceipt
{
    Schema = schema,
    Source = "anonymous-ephemeral-browser",
    TargetHost = "chatgpt.com",
    AuthSupplied = false,
    PersistentProfile = false,
    PromptAttempts = 0,
    FinalState = "NOT_STARTED",
    TimestampUtc = DateTimeOffset.UtcNow
};

try
{
    await using var session = await BrowserSession.StartAsync(headless: true);
    var page = session.Page;
    page.SetDefaultTimeout(8_000);
    result.BrowserLaunched = true;
    try
    {
        await page.GotoAsync(target, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 40_000
        });
    }
    catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
    {
        result.FinalState = "NAVIGATION_FAILED";
        result.FailureClass = ex.GetType().Name; // no exception text or raw page content
        await WriteAndExitAsync(result, output);
        return 0;
    }

    await page.WaitForTimeoutAsync(4_000);
    result.FinalHost = SafeHost(page.Url);
    result.PageReached = result.FinalHost == "chatgpt.com";
    if (!result.PageReached)
    {
        result.FinalState = "REDIRECTED_OFF_TARGET";
        await WriteAndExitAsync(result, output);
        return 0;
    }

    var body = await ReadPageTextAsync(page);
    if (Classify(body, false) == "CHALLENGE_OR_BROWSER_CHECK")
    {
        result.FinalState = "CHALLENGE_OR_BROWSER_CHECK";
        await WriteAndExitAsync(result, output);
        return 0;
    }

    // Only first-party visible, semantically plausible ChatGPT composer candidates.
    var composer = await FindFirstVisibleAsync(page,
        "#prompt-textarea",
        "[data-testid='prompt-textarea']",
        "textarea[placeholder]",
        "[contenteditable='true'][role='textbox']");
    if (composer is null)
    {
        result.FinalState = Classify(body, false);
        await WriteAndExitAsync(result, output);
        return 0;
    }

    result.ComposerVisible = true;
    result.FinalState = "GUEST_COMPOSER_VISIBLE";

    // Qualified guest observation: one harmless prompt, never retries or login/MFA.
    // The button must become a visible, enabled, recognized Send control.
    try
    {
        await composer.FillAsync(prompt);
    }
    catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
    {
        result.FinalState = "COMPOSER_NOT_WRITABLE";
        result.FailureClass = ex.GetType().Name;
        await WriteAndExitAsync(result, output);
        return 0;
    }

    var send = await FindFirstVisibleAsync(page,
        "button[data-testid='send-button']",
        "button[data-testid='composer-submit-button']",
        "button[aria-label='Send prompt']",
        "button[aria-label='Send message']");
    if (send is null || !await send.IsEnabledAsync())
    {
        result.FinalState = "SEND_CONTROL_UNAVAILABLE";
        await WriteAndExitAsync(result, output);
        return 0;
    }

    // We cannot infer submission from an enabled control. Click exactly once.
    var previousAssistantTurns = await page.Locator("[data-message-author-role='assistant']").CountAsync();
    result.PromptAttempts = 1;
    try { await send.ClickAsync(new LocatorClickOptions { Timeout = 8_000 }); }
    catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
    {
        result.FinalState = "SUBMISSION_UNCERTAIN";
        result.FailureClass = ex.GetType().Name;
        await WriteAndExitAsync(result, output);
        return 0;
    }

    var deadline = DateTimeOffset.UtcNow.AddSeconds(observationSeconds);
    while (DateTimeOffset.UtcNow < deadline)
    {
        await page.WaitForTimeoutAsync(1_000);
        if (SafeHost(page.Url) != "chatgpt.com")
        {
            result.FinalState = "NAVIGATION_CHANGED_AFTER_SUBMISSION";
            break;
        }
        var assistants = page.Locator("[data-message-author-role='assistant']");
        var count = await assistants.CountAsync();
        if (count > previousAssistantTurns)
        {
            result.AssistantTurnObserved = true;
            var newTurn = assistants.Last;
            var text = await newTurn.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 5_000 });
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                result.MarkerObservedInAssistantTurn = true;
                result.FinalState = "GUEST_TURN_CORRELATED";
                break;
            }
        }
        var state = Classify(await ReadPageTextAsync(page), false);
        if (state is "CHALLENGE_OR_BROWSER_CHECK" or "LOGIN_GATE")
        {
            result.FinalState = state;
            break;
        }
    }

    if (result.FinalState == "GUEST_COMPOSER_VISIBLE")
        result.FinalState = result.AssistantTurnObserved
            ? "ASSISTANT_TURN_OBSERVED_MARKER_MISSING"
            : "ASSISTANT_TURN_NOT_OBSERVED";

    await WriteAndExitAsync(result, output);
    return 0;
}
catch (Exception ex)
{
    result.FinalState = "HARNESS_FAILURE";
    result.FailureClass = ex.GetType().Name;
    await WriteAndExitAsync(result, output);
    return 2;
}

static string Classify(string body, bool composerVisible)
{
    if (Regex.IsMatch(body, @"(?i)\b(just a moment|checking your browser|verify you are human|captcha|access denied|unusual traffic)\b"))
        return "CHALLENGE_OR_BROWSER_CHECK";
    if (composerVisible) return "GUEST_COMPOSER_VISIBLE";
    if (Regex.IsMatch(body, @"(?i)\b(log in|login|sign up|sign in)\b")) return "LOGIN_GATE";
    return "COMPOSER_NOT_FOUND";
}

static async Task<string> ReadPageTextAsync(IPage page)
{
    try
    {
        var content = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 4_000 });
        return content.Length > 12_000 ? content[..12_000] : content;
    }
    catch (PlaywrightException) { return ""; }
}

static async Task<ILocator?> FindFirstVisibleAsync(IPage page, params string[] selectors)
{
    foreach (var selector in selectors)
    {
        var all = page.Locator(selector);
        var n = Math.Min(await all.CountAsync(), 8);
        for (var i = 0; i < n; i++)
        {
            var candidate = all.Nth(i);
            if (await candidate.IsVisibleAsync()) return candidate;
        }
    }
    return null;
}

static string SafeHost(string url)
{
    return Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        ? parsed.Host.ToLowerInvariant() : "invalid";
}

static async Task WriteAndExitAsync(ProbeReceipt receipt, string? output)
{
    receipt.TimestampUtc = DateTimeOffset.UtcNow;
    var json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    });
    Console.WriteLine(json);
    if (!string.IsNullOrWhiteSpace(output))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, json + "\n");
    }
}

internal sealed class ProbeReceipt
{
    public string Schema { get; set; } = "";
    public string Source { get; set; } = "";
    public string TargetHost { get; set; } = "";
    public string? FinalHost { get; set; }
    public bool AuthSupplied { get; set; }
    public bool PersistentProfile { get; set; }
    public bool BrowserLaunched { get; set; }
    public bool PageReached { get; set; }
    public bool ComposerVisible { get; set; }
    public int PromptAttempts { get; set; }
    public bool AssistantTurnObserved { get; set; }
    public bool MarkerObservedInAssistantTurn { get; set; }
    public string FinalState { get; set; } = "";
    public string? FailureClass { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
}
