using AiProxy.Application.Interfaces;
using AiProxy.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AiProxy.Pages;

public sealed class SettingsModel : PageModel
{
    private readonly IAdminService _admin;
    [BindProperty] public AdminSettings Settings { get; set; } = new();
    public bool Saved { get; private set; }
    public SettingsModel(IAdminService admin) => _admin = admin;
    public void OnGet() => Settings = _admin.GetSettings();
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var existing = _admin.GetSettings();
        Settings.Providers = existing.Providers;
        Settings.Models = existing.Models;
        Settings.M365Copilot = existing.M365Copilot;
        await _admin.SaveSettingsAsync(Settings, cancellationToken);
        Settings = _admin.GetSettings();
        Saved = true;
        return Page();
    }
}
