using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiProxy.Configuration;
using AiProxy.Contracts;
using Microsoft.Playwright;

namespace AiProxy.Services;

/// <summary>
/// Owns an AiProxy-only Chromium profile used for M365 Copilot attachments.  The profile stays
/// on this machine: cookies are deliberately never read, returned, or put in configuration.
/// </summary>
public interface IM365CopilotBrowserClient
{
    bool UsesBrowserSession(string clientSessionId);
    Task<string> ExecuteAsync(string clientSessionId, string prompt, IReadOnlyList<string> imagePaths, CancellationToken cancellationToken = default);
    Task<M365BrowserProfileStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

public sealed record M365BrowserProfileStatus(bool IsConfigured, bool IsActive, string Message);

public sealed class M365CopilotBrowserClient : IM365CopilotBrowserClient, IAsyncDisposable
{
    private const string ChatUrl = "https://m365.cloud.microsoft/chat";
    private const char RecordSeparator = '\u001e';
    private static readonly string[] ComposerSelectors =
    {
        "[role='textbox'][contenteditable='true']",
        "textarea[placeholder*='message' i]",
        "textarea",
        "[contenteditable='true']"
    };

    private readonly IRuntimeSettings _settings;
    private readonly ILogger<M365CopilotBrowserClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, BrowserSession> _sessions = new(StringComparer.Ordinal);
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private bool _isActive;

    public M365CopilotBrowserClient(IRuntimeSettings settings, ILogger<M365CopilotBrowserClient> logger) =>
        (_settings, _logger) = (settings, logger);

    public bool UsesBrowserSession(string clientSessionId) => _sessions.ContainsKey(clientSessionId);

    public async Task<M365BrowserProfileStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var profile = GetProfileDirectory();
        var configured = Directory.Exists(profile) && Directory.EnumerateFileSystemEntries(profile).Any();
        return await Task.FromResult(new M365BrowserProfileStatus(
            configured,
            _isActive,
            configured
                ? "Den dedikerte nettleserprofilen finnes lokalt. AiProxy viser eller eksporterer aldri cookieverdier."
                : "Koble til nettleserprofilen før du sender bilder til M365 Copilot."));
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await CloseContextAsync();
            var context = await LaunchContextAsync(headless: false, cancellationToken);
            try
            {
                var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
                await page.GotoAsync(ChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
                await EnsureChatReadyAsync(page, TimeSpan.FromMinutes(5), cancellationToken);
            }
            finally
            {
                await context.CloseAsync();
            }

            _logger.LogInformation("M365-nettleserprofilen er koblet til uten å eksportere cookie-data");
        }
        catch (PlaywrightException ex)
        {
            throw new ImageInputException($"Kunne ikke klargjøre M365-nettleserprofilen: {ex.Message}");
        }
        finally
        {
            await ClosePlaywrightIfUnusedAsync();
            _gate.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await CloseContextAsync();
            var profile = GetProfileDirectory();
            if (Directory.Exists(profile))
                Directory.Delete(profile, recursive: true);
            _sessions.Clear();
            _logger.LogInformation("Den dedikerte M365-nettleserprofilen ble fjernet lokalt");
        }
        finally
        {
            await ClosePlaywrightIfUnusedAsync();
            _gate.Release();
        }
    }

    public async Task<string> ExecuteAsync(string clientSessionId, string prompt, IReadOnlyList<string> imagePaths, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = await GetOrCreateSessionAsync(clientSessionId, cancellationToken);
            var composer = await EnsureChatReadyAsync(session.Page, TimeSpan.FromSeconds(30), cancellationToken);
            if (imagePaths.Count > 0)
                await UploadImagesAsync(session.Page, composer, imagePaths, cancellationToken);

            var turn = new BrowserTurn();
            session.CurrentTurn = turn;
            await composer.ClickAsync();
            await composer.FillAsync(prompt);
            await composer.PressAsync("Enter");

            try
            {
                return await turn.Completion.Task.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new ImageInputException("M365 Copilot svarte ikke fra nettleseren innen fem minutter.");
            }
            finally
            {
                session.CurrentTurn = null;
            }
        }
        catch (PlaywrightException ex)
        {
            throw new ImageInputException($"M365-nettleserprofilen kunne ikke sende bildet. Åpne Admin / M365 og velg «Koble til bildeopplasting». ({ex.Message})");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BrowserSession> GetOrCreateSessionAsync(string clientSessionId, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(clientSessionId, out var existing) && !existing.Page.IsClosed)
            return existing;

        var context = await EnsureContextAsync(cancellationToken);
        var page = await context.NewPageAsync();
        var session = new BrowserSession(page);
        page.WebSocket += (_, socket) =>
        {
            if (!socket.Url.Contains("chathub", StringComparison.OrdinalIgnoreCase))
                return;
            socket.FrameReceived += (_, frame) => HandleFrame(session, frame.Text);
        };
        await page.GotoAsync(ChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
        _sessions[clientSessionId] = session;
        return session;
    }

    private async Task<IBrowserContext> EnsureContextAsync(CancellationToken cancellationToken)
    {
        if (_context != null)
            return _context;
        _context = await LaunchContextAsync(headless: true, cancellationToken);
        _isActive = true;
        return _context;
    }

    private async Task<IBrowserContext> LaunchContextAsync(bool headless, CancellationToken cancellationToken)
    {
        _playwright ??= await Playwright.CreateAsync();
        Directory.CreateDirectory(GetProfileDirectory());
        return await _playwright.Chromium.LaunchPersistentContextAsync(GetProfileDirectory(), new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = headless,
            Locale = _settings.Current.M365Copilot.Locale,
            Timeout = 60_000
        });
    }

    private async Task<ILocator> EnsureChatReadyAsync(IPage page, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await SelectKnownAccountAsync(page);
            foreach (var selector in ComposerSelectors)
            {
                var composer = page.Locator(selector).First;
                if (await composer.IsVisibleAsync() &&
                    Uri.TryCreate(page.Url, UriKind.Absolute, out var currentUrl) &&
                    currentUrl.Host.EndsWith("m365.cloud.microsoft", StringComparison.OrdinalIgnoreCase))
                    return composer;
            }
            await Task.Delay(500, cancellationToken);
        }
        throw new ImageInputException("Fant ikke M365 Copilot-skrivefeltet. Fullfør innloggingen i det åpne nettleservinduet og prøv igjen.");
    }

    private static async Task SelectKnownAccountAsync(IPage page)
    {
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var url) ||
            !url.Host.EndsWith("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
            return;

        var accountByEmail = page.GetByText(new Regex(@"^[^@\s]+@[^@\s]+$")).First;
        if (await accountByEmail.IsVisibleAsync())
        {
            await accountByEmail.ClickAsync();
            return;
        }

        foreach (var selector in new[] { "#idDiv .table[role='button']", "#idDiv .table", "#idDiv [role='button']" })
        {
            var account = page.Locator(selector).First;
            if (await account.IsVisibleAsync())
            {
                await account.ClickAsync();
                return;
            }
        }
    }

    private async Task UploadImagesAsync(IPage page, ILocator composer, IReadOnlyList<string> imagePaths, CancellationToken cancellationToken)
    {
        var inputs = page.Locator("input[type='file']");
        var uploadedWithNativeChooser = false;
        if (await inputs.CountAsync() == 0)
        {
            var attachmentButton = await FindAttachmentButtonAsync(page);
            if (attachmentButton != null)
            {
                await attachmentButton.ClickAsync();
                await page.WaitForTimeoutAsync(150);
                uploadedWithNativeChooser = await ActivateLocalUploadAsync(page, imagePaths);
            }
            else
            {
                // Copilot creates its hidden file input only after the plus button beside the
                // composer is activated. This fallback covers controls without an accessible name.
                var bounds = await composer.BoundingBoxAsync();
                if (bounds != null)
                {
                    // Depending on Copilot's current DOM, the textbox locator starts at the
                    // text area or at the whole composer row. The plus icon is respectively 26px
                    // to the left or 32px inside that row.
                    foreach (var x in new[] { Math.Max(0, bounds.X - 26), bounds.X + 32 })
                    {
                        await page.Mouse.ClickAsync(x, bounds.Y + bounds.Height / 2);
                        await page.WaitForTimeoutAsync(150);
                        if (await inputs.CountAsync() > 0)
                            break;

                        // The first Add-menu action is the local upload action. Selecting it
                        // before trying a second coordinate avoids immediately closing the menu.
                        await page.Keyboard.PressAsync("ArrowDown");
                        await page.Keyboard.PressAsync("Enter");
                        await page.WaitForTimeoutAsync(300);
                        if (await TrySetFilesFromFileChooserAsync(page, imagePaths))
                            return;
                        if (await inputs.CountAsync() > 0)
                            break;
                    }
                }
            }
            await page.WaitForTimeoutAsync(250);
        }
        if (await inputs.CountAsync() == 0)
        {
            if (uploadedWithNativeChooser)
            {
                await page.WaitForTimeoutAsync(750);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            try
            {
                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = Path.Combine(Path.GetTempPath(), "aiproxy-m365-attachment-menu.png"),
                    FullPage = true
                });
            }
            catch (PlaywrightException)
            {
                // Diagnostics must not change the client-facing upload error.
            }
            await LogVisibleButtonsAsync(page);
            throw new ImageInputException("Fant ikke filvelgeren i M365 Copilot. Profilen mangler tilgang til bildeopplasting.");
        }

        await inputs.First.SetInputFilesAsync(imagePaths);
        // The web app owns the upload request. Waiting briefly for the attachment UI prevents
        // Enter from sending before the selected files have reached the composer.
        await page.WaitForTimeoutAsync(750);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task<bool> ActivateLocalUploadAsync(IPage page, IReadOnlyList<string> imagePaths)
    {
        // The PlusMenuButton opens a menu; it does not contain the file input itself.  M365 has
        // used both a hidden input and a native file chooser for this action, so support both.
        var uploadAction = page.Locator("[role='menuitem'], [role='option']")
            .Filter(new LocatorFilterOptions
            {
                HasTextRegex = new Regex("upload|device|computer|from this device", RegexOptions.IgnoreCase)
            })
            .First;

        if (await uploadAction.IsVisibleAsync())
        {
            var chooser = page.WaitForFileChooserAsync(new PageWaitForFileChooserOptions { Timeout = 3_000 });
            await uploadAction.ClickAsync();
            try
            {
                await (await chooser).SetFilesAsync(imagePaths);
                return true;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                // This M365 build uses an input element instead of opening the native chooser.
            }
            return false;
        }

        // The local-upload entry is the first entry in the Add menu.  Keyboard navigation also
        // works for M365 versions whose menu items have no stable accessible labels.
        var keyboardChooser = page.WaitForFileChooserAsync(new PageWaitForFileChooserOptions { Timeout = 3_000 });
        await page.Keyboard.PressAsync("ArrowDown");
        await page.Keyboard.PressAsync("Enter");
        try
        {
            await (await keyboardChooser).SetFilesAsync(imagePaths);
            return true;
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            // The caller will use the dynamic input when this menu variant creates one.
            return false;
        }
    }

    private static async Task<bool> TrySetFilesFromFileChooserAsync(IPage page, IReadOnlyList<string> imagePaths)
    {
        try
        {
            var chooser = await page.WaitForFileChooserAsync(new PageWaitForFileChooserOptions { Timeout = 500 });
            await chooser.SetFilesAsync(imagePaths);
            return true;
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            return false;
        }
    }

    private static async Task<ILocator?> FindAttachmentButtonAsync(IPage page)
    {
        foreach (var selector in new[]
                 {
                     "button[data-testid='PlusMenuButton']",
                     "button[aria-label*='add' i]", "button[aria-label*='attach' i]", "button[aria-label*='upload' i]",
                     "[role='button'][aria-label*='add' i]", "[role='button'][aria-label*='attach' i]", "[role='button'][aria-label*='upload' i]",
                     "button[title*='add' i]", "button[title*='attach' i]", "button[title*='upload' i]"
                 })
        {
            var candidate = page.Locator(selector).First;
            if (await candidate.IsVisibleAsync())
                return candidate;
        }
        return null;
    }

    private async Task LogVisibleButtonsAsync(IPage page)
    {
        var controls = page.Locator("button, [role='button']");
        var labels = new List<string>();
        for (var index = 0; index < Math.Min(await controls.CountAsync(), 40); index++)
        {
            var control = controls.Nth(index);
            if (!await control.IsVisibleAsync())
                continue;
            var label = await control.GetAttributeAsync("aria-label") ?? await control.GetAttributeAsync("title") ?? await control.InnerTextAsync();
            if (!string.IsNullOrWhiteSpace(label))
                labels.Add(label.Trim());
        }
        _logger.LogWarning("Fant ikke M365-filvelger etter vedleggsmenyen. Synlige knapper: {Buttons}", string.Join(" | ", labels));
        var addControl = await page.EvaluateAsync<string>("""
            () => document.elementsFromPoint(390, 474)
                .slice(0, 6)
                .map(element => element.outerHTML.slice(0, 800))
                .join("\n")
            """);
        _logger.LogWarning("M365 Add-kontrollens DOM: {AddControl}", addControl);
    }

    private static void HandleFrame(BrowserSession session, string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || session.CurrentTurn == null)
            return;

        var turn = session.CurrentTurn;
        try
        {
            foreach (var frame in payload.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                using var document = JsonDocument.Parse(frame);
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetInt32() : -1;
                if (type == 1 && root.TryGetProperty("arguments", out var arguments))
                    foreach (var argument in arguments.EnumerateArray())
                    {
                        if (argument.TryGetProperty("writeAtCursor", out var delta) && delta.ValueKind == JsonValueKind.String)
                            AppendCandidate(turn, turn.Answer + delta.GetString());
                        else
                            AppendMessages(turn, argument);
                    }
                if (type == 2)
                {
                    if (root.TryGetProperty("item", out var item))
                        AppendMessages(turn, item);
                    Complete(turn);
                }
                if (type is 3 or 7)
                    Complete(turn);
            }
        }
        catch (JsonException)
        {
            // Non-JSON diagnostic frames do not describe a model response.
        }
    }

    private static void AppendMessages(BrowserTurn turn, JsonElement container)
    {
        if (!container.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return;
        foreach (var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("author", out var author) || author.GetString() != "bot" ||
                !message.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String ||
                (message.TryGetProperty("messageType", out var messageType) && messageType.ValueKind == JsonValueKind.String))
                continue;
            AppendCandidate(turn, text.GetString()!);
        }
    }

    private static void AppendCandidate(BrowserTurn turn, string candidate)
    {
        if (candidate.Length > turn.Answer.Length && candidate.StartsWith(turn.Answer, StringComparison.Ordinal))
            turn.Answer = candidate;
    }

    private static void Complete(BrowserTurn turn)
    {
        if (string.IsNullOrWhiteSpace(turn.Answer))
            turn.Completion.TrySetException(new ImageInputException("M365 Copilot fullførte uten et svar."));
        else
            turn.Completion.TrySetResult(turn.Answer);
    }

    private async Task CloseContextAsync()
    {
        if (_context != null)
        {
            await _context.CloseAsync();
            _context = null;
        }
        _isActive = false;
        _sessions.Clear();
    }

    private async Task ClosePlaywrightIfUnusedAsync()
    {
        if (_context == null && _playwright != null)
        {
            _playwright.Dispose();
            _playwright = null;
            await Task.CompletedTask;
        }
    }

    private string GetProfileDirectory()
    {
        var root = _settings.Current.M365Copilot.CacheDirectory;
        root = string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiProxy")
            : root;
        return Path.Combine(Path.GetFullPath(root), "m365-browser-profile");
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { await CloseContextAsync(); await ClosePlaywrightIfUnusedAsync(); }
        finally { _gate.Release(); _gate.Dispose(); }
    }

    private sealed class BrowserSession(IPage page)
    {
        public IPage Page { get; } = page;
        public BrowserTurn? CurrentTurn { get; set; }
    }

    private sealed class BrowserTurn
    {
        public string Answer { get; set; } = string.Empty;
        public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
