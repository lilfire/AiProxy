using System.Text.Json;
using AiProxy.Configuration;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensibility;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.Playwright;

namespace AiProxy.Services;

public sealed class M365CopilotTokenProvider
{
    private const string ClientId = "c0ab8ce9-e9a0-42e7-b064-33d422df41f1";
    private const string Authority = "https://login.microsoftonline.com/common";
    // This is a Microsoft-owned public client. It has nativeclient registered, but not a loopback URI.
    // Nativeclient responses therefore have to be handled by a custom browser UI (see NativeClientWebUi).
    private const string RedirectUri = "https://login.microsoftonline.com/common/oauth2/nativeclient";
    private const string LegacyCacheFileName = "msal-cache.json";
    private static readonly string[] Scopes =
    {
        "https://substrate.office.com/sydney/M365Chat.Read",
        "https://substrate.office.com/sydney/sydney.readwrite"
    };

    private readonly IRuntimeSettings _settings;
    private readonly ILogger<M365CopilotTokenProvider> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly Lazy<Task<IPublicClientApplication>> _application;

    public M365CopilotTokenProvider(IRuntimeSettings settings, ILogger<M365CopilotTokenProvider> logger)
    {
        _settings = settings;
        _logger = logger;
        _application = new Lazy<Task<IPublicClientApplication>>(CreateApplicationAsync);
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        await _tokenLock.WaitAsync(cancellationToken);

        try
        {
            // Reuse the cache maintained by the working m365-copilot-cli when it
            // is available. The two MSAL implementations persist their encrypted
            // local caches differently, but the CLI cache contains the same
            // first-party Sydney access token used by its proven ChatHub client.
            var legacyToken = TryGetLegacyToken();
            if (legacyToken != null)
            {
                _logger.LogInformation("Bruker gyldig M365-token fra m365-copilot-cli-cachen");
                return legacyToken;
            }

            var application = await _application.Value;
            var account = (await application.GetAccountsAsync()).FirstOrDefault();

            if (account != null)
            {
                try
                {
                    var result = await application.AcquireTokenSilent(Scopes, account).ExecuteAsync(cancellationToken);
                    return result.AccessToken;
                }
                catch (MsalUiRequiredException)
                {
                    _logger.LogInformation("M365 Copilot-token må fornyes gjennom innlogging");
                }
            }

            if (!_settings.Current.M365Copilot.EnableInteractiveLogin)
            {
                throw new InvalidOperationException(
                    "M365 Copilot er ikke innlogget. Sett AiProxy:M365Copilot:EnableInteractiveLogin til true og start tjenesten fra en interaktiv Windows-sesjon.");
            }

            _logger.LogInformation("Åpner Microsoft-innlogging for M365 Copilot");
            var interactiveResult = await application
                .AcquireTokenInteractive(Scopes)
                .WithPrompt(Prompt.SelectAccount)
                .WithCustomWebUi(new NativeClientWebUi(_logger))
                .ExecuteAsync(cancellationToken);

            return interactiveResult.AccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async Task<M365LoginStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (TryGetLegacyToken() != null)
            return new M365LoginStatus(true, "m365-copilot-cli-cache");

        var application = await _application.Value;
        var account = (await application.GetAccountsAsync()).FirstOrDefault();
        return new M365LoginStatus(account != null, account?.Username);
    }

    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        var application = await _application.Value;
        await application.AcquireTokenInteractive(Scopes)
            .WithPrompt(Prompt.SelectAccount)
            .WithCustomWebUi(new NativeClientWebUi(_logger))
            .ExecuteAsync(cancellationToken);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var application = await _application.Value;
        foreach (var account in await application.GetAccountsAsync())
            await application.RemoveAsync(account);

        var cachePath = Path.Combine(GetCacheDirectory(), "m365-copilot-msal.cache");
        if (File.Exists(cachePath))
            File.Delete(cachePath);
    }

    private async Task<IPublicClientApplication> CreateApplicationAsync()
    {
        var application = PublicClientApplicationBuilder
            .Create(ClientId)
            .WithAuthority(Authority)
            .WithRedirectUri(RedirectUri)
            .Build();

        var cacheDirectory = GetCacheDirectory();

        Directory.CreateDirectory(cacheDirectory);
        var storageProperties = new StorageCreationPropertiesBuilder("m365-copilot-msal.cache", cacheDirectory)
            .Build();
        var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties);
        cacheHelper.RegisterCache(application.UserTokenCache);

        return application;
    }

    private string GetCacheDirectory()
    {
        var configuredDirectory = _settings.Current.M365Copilot.CacheDirectory;
        return string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiProxy")
            : configuredDirectory;
    }

    private static string GetLegacyCachePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config",
        "opencode-m365",
        LegacyCacheFileName);

    private static string? TryGetLegacyToken()
    {
        var cachePath = GetLegacyCachePath();
        if (!File.Exists(cachePath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(cachePath));
            if (!document.RootElement.TryGetProperty("AccessToken", out var accessTokens) ||
                accessTokens.ValueKind != JsonValueKind.Object)
                return null;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return accessTokens.EnumerateObject()
                .Select(entry => entry.Value)
                .Select(token => new
                {
                    Secret = GetString(token, "secret"),
                    ClientId = GetString(token, "client_id"),
                    Target = GetString(token, "target"),
                    ExpiresOn = GetUnixTime(token, "expires_on")
                })
                .Where(token => token.ClientId == ClientId &&
                                token.Target?.Contains("M365Chat.Read", StringComparison.OrdinalIgnoreCase) == true &&
                                token.Target?.Contains("sydney.readwrite", StringComparison.OrdinalIgnoreCase) == true &&
                                token.ExpiresOn > now + 60 &&
                                !string.IsNullOrWhiteSpace(token.Secret))
                .OrderByDescending(token => token.ExpiresOn)
                .Select(token => token.Secret)
                .FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long GetUnixTime(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return 0;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(property.GetString(), out var value) => value,
            _ => 0
        };
    }

    /// <summary>
    /// Captures the authorization-code navigation to the Microsoft-owned nativeclient URI.
    /// A system browser cannot do this because the URI is not a loopback callback.
    /// </summary>
    private sealed class NativeClientWebUi(ILogger logger) : ICustomWebUi
    {
        private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(10);

        public async Task<Uri> AcquireAuthorizationCodeAsync(
            Uri authorizationUri,
            Uri redirectUri,
            CancellationToken cancellationToken)
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false
            });

            var page = await browser.NewPageAsync();
            var authorizationCodeUri = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);

            page.Request += (_, request) =>
            {
                if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var requestUri)
                    || !requestUri.AbsoluteUri.StartsWith(redirectUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrEmpty(requestUri.Query))
                    return;

                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(requestUri.Query);
                if (query.ContainsKey("code"))
                    authorizationCodeUri.TrySetResult(requestUri);
            };

            _ = page.GotoAsync(authorizationUri.AbsoluteUri, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded
            }).ContinueWith(
                task => logger.LogDebug(task.Exception, "M365-påloggingsnavigasjon ble avbrutt etter redirect"),
                TaskContinuationOptions.OnlyOnFaulted);

            try
            {
                return await authorizationCodeUri.Task.WaitAsync(SignInTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new MsalClientException(
                    "m365_interactive_login_timeout",
                    "Tidsavbrudd ved M365-innlogging. Fullfør innloggingen i nettleservinduet og prøv på nytt.");
            }
        }
    }
}

public sealed record M365LoginStatus(bool IsSignedIn, string? Username);
