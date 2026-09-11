using AiProxy.Application.Interfaces;
using AiProxy.Configuration;
using AiProxy.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AiProxy.Pages;

public sealed class M365Model : PageModel
{
    private readonly IAdminService _admin;
    private readonly M365CopilotTokenProvider _tokenProvider;
    private readonly IM365CopilotBrowserClient _browserClient;
    [BindProperty] public M365CopilotOptions Settings { get; set; } = new();
    public M365LoginStatus Status { get; private set; } = new(false, null);
    public M365BrowserProfileStatus BrowserStatus { get; private set; } = new(false, false, "");
    public string? Message { get; private set; }
    public M365Model(IAdminService admin, M365CopilotTokenProvider tokenProvider, IM365CopilotBrowserClient browserClient) => (_admin, _tokenProvider, _browserClient) = (admin, tokenProvider, browserClient);
    public async Task OnGetAsync(CancellationToken ct) { await RefreshAsync(ct); }
    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct) { await SaveAsync(ct); await RefreshAsync(ct); Message = "M365-innstillingene er lagret."; return Page(); }
    public async Task<IActionResult> OnPostSignInAsync(CancellationToken ct)
    {
        await SaveAsync(ct);
        try { await _tokenProvider.SignInAsync(ct); Message = "M365-innloggingen er fullført."; } catch (Exception ex) { Message = $"Innlogging feilet: {ex.Message}"; }
        await RefreshAsync(ct); return Page();
    }
    public async Task<IActionResult> OnPostSignOutAsync(CancellationToken ct) { await _tokenProvider.SignOutAsync(ct); await RefreshAsync(ct); Message = "Lokal M365-token-cache er fjernet."; return Page(); }
    public async Task<IActionResult> OnPostConnectImageUploadAsync(CancellationToken ct)
    {
        try { await _browserClient.ConnectAsync(ct); Message = "Bildeprofilen er koblet til og kan brukes av M365 Copilot."; }
        catch (Exception ex) { Message = $"Tilkobling av bildeprofil feilet: {ex.Message}"; }
        await RefreshAsync(ct); return Page();
    }
    public async Task<IActionResult> OnPostSignOutImageUploadAsync(CancellationToken ct)
    {
        try { await _browserClient.SignOutAsync(ct); Message = "Den lokale M365-bildeprofilen er fjernet."; }
        catch (Exception ex) { Message = $"Kunne ikke fjerne bildeprofilen: {ex.Message}"; }
        await RefreshAsync(ct); return Page();
    }
    private async Task SaveAsync(CancellationToken ct) { var all = _admin.GetSettings(); Settings.Enabled = all.Providers.TryGetValue("M365", out var enabled) && enabled; all.M365Copilot = Settings; await _admin.SaveSettingsAsync(all, ct); }
    private async Task RefreshAsync(CancellationToken ct) { Settings = _admin.GetSettings().M365Copilot; Status = await _tokenProvider.GetStatusAsync(ct); BrowserStatus = await _browserClient.GetStatusAsync(ct); }
}
