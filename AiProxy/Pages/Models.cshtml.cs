using AiProxy.Application.Interfaces;
using AiProxy.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AiProxy.Pages;

public sealed class ModelsModel : PageModel
{
    private readonly IAdminService _admin;
    [BindProperty] public List<ModelInput> Models { get; set; } = [];
    public bool Saved { get; private set; }
    public ModelsModel(IAdminService admin) => _admin = admin;
    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var settings = _admin.GetSettings();
        var activeSettings = Models.Select(model => new AdminModelSetting { ProviderName = model.ProviderName, ModelId = model.ModelId, Alias = model.Alias?.Trim() ?? string.Empty, Enabled = model.Enabled });
        var inactiveSettings = settings.Models.Where(model => !settings.Providers.TryGetValue(model.ProviderName, out var enabled) || !enabled);
        settings.Models = activeSettings.Concat(inactiveSettings).ToList();
        await _admin.SaveSettingsAsync(settings, cancellationToken);
        Saved = true;
        await LoadAsync(cancellationToken);
        return Page();
    }
    public async Task<IActionResult> OnPostRefreshAsync(CancellationToken cancellationToken)
    {
        await _admin.RefreshModelsAsync(cancellationToken);
        return RedirectToPage();
    }
    private async Task LoadAsync(CancellationToken ct)
    {
        var settings = _admin.GetSettings();
        var models = await _admin.GetDiscoveredModelsAsync(ct);
        Models = models.Select(model =>
        {
            var raw = settings.Models.FirstOrDefault(item => item.ProviderName.Equals(model.ProviderName, StringComparison.OrdinalIgnoreCase) &&
                item.ModelId.Equals(model.ModelId, StringComparison.OrdinalIgnoreCase));
            return new ModelInput { ProviderName = model.ProviderName, ModelId = model.ModelId, Alias = raw?.Alias ?? string.Empty, Enabled = raw?.Enabled ?? true };
        }).OrderBy(model => model.ProviderName).ThenBy(model => model.ModelId).ToList();
    }
    public sealed class ModelInput { public string ProviderName { get; set; } = string.Empty; public string ModelId { get; set; } = string.Empty; public bool Enabled { get; set; } = true; public string? Alias { get; set; } }
}
