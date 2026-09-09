using AiProxy.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AiProxy.Pages;

public sealed class ProvidersModel : PageModel
{
    private readonly IAdminService _admin;
    [BindProperty] public List<ProviderInput> Providers { get; set; } = [];
    public bool Saved { get; private set; }
    public ProvidersModel(IAdminService admin) => _admin = admin;
    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var settings = _admin.GetSettings();
        foreach (var provider in Providers) settings.Providers[provider.Name] = provider.Enabled;
        await _admin.SaveSettingsAsync(settings, cancellationToken);
        Saved = true;
        await LoadAsync(cancellationToken);
        return Page();
    }
    private async Task LoadAsync(CancellationToken ct) => Providers = (await _admin.GetProviderStatusesAsync(ct))
        .Select(item => new ProviderInput { Name = item.Name, Enabled = item.Enabled, Available = item.Available, Detail = item.Detail, Usage = item.Usage, Quota = item.Quota }).ToList();
    public sealed class ProviderInput { public string Name { get; set; } = string.Empty; public bool Enabled { get; set; } public bool Available { get; set; } public string Detail { get; set; } = string.Empty; public ProviderUsageSnapshot Usage { get; set; } = new(0, 0, null); public ProviderQuotaSnapshot? Quota { get; set; } }
}
