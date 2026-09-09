using AiProxy.Application.Interfaces;
using AiProxy.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AiProxy.Pages;

public sealed class IndexModel : PageModel
{
    private readonly IAdminService _admin;
    private readonly IChatProviderRegistry _registry;
    private readonly M365CopilotTokenProvider _m365TokenProvider;
    public IReadOnlyList<AdminProviderStatus> Providers { get; private set; } = [];
    public int ModelCount { get; private set; }
    public M365LoginStatus M365Status { get; private set; } = new(false, null);
    public IndexModel(IAdminService admin, IChatProviderRegistry registry, M365CopilotTokenProvider m365TokenProvider) => (_admin, _registry, _m365TokenProvider) = (admin, registry, m365TokenProvider);
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Providers = await _admin.GetProviderStatusesAsync(cancellationToken);
        ModelCount = (await _registry.GetModelsAsync(cancellationToken)).Count;
        M365Status = await _m365TokenProvider.GetStatusAsync(cancellationToken);
    }
}
