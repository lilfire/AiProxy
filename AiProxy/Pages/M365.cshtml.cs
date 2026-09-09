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
    [BindProperty] public M365CopilotOptions Settings { get; set; } = new();
    public M365LoginStatus Status { get; private set; } = new(false, null);
    public string? Message { get; private set; }
    public M365Model(IAdminService admin, M365CopilotTokenProvider tokenProvider) => (_admin, _tokenProvider) = (admin, tokenProvider);
    public async Task OnGetAsync(CancellationToken ct) { Settings = _admin.GetSettings().M365Copilot; Status = await _tokenProvider.GetStatusAsync(ct); }
    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct) { await SaveAsync(ct); Message = "M365-innstillingene er lagret."; return Page(); }
    public async Task<IActionResult> OnPostSignInAsync(CancellationToken ct)
    {
        await SaveAsync(ct);
        try { await _tokenProvider.SignInAsync(ct); Message = "M365-innloggingen er fullført."; } catch (Exception ex) { Message = $"Innlogging feilet: {ex.Message}"; }
        Status = await _tokenProvider.GetStatusAsync(ct); return Page();
    }
    public async Task<IActionResult> OnPostSignOutAsync(CancellationToken ct) { await _tokenProvider.SignOutAsync(ct); Status = await _tokenProvider.GetStatusAsync(ct); Message = "Lokal M365-token-cache er fjernet."; return Page(); }
    private async Task SaveAsync(CancellationToken ct) { var all = _admin.GetSettings(); Settings.Enabled = all.Providers.TryGetValue("M365", out var enabled) && enabled; all.M365Copilot = Settings; await _admin.SaveSettingsAsync(all, ct); }
}
