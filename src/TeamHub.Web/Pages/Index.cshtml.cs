using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Web.Home;
using TeamHub.Authentication;
using TeamHub.Web.AccessControl;

namespace TeamHub.Web.Pages;

public class IndexModel(IHomeContentService homeContentService, ICurrentAccessService currentAccess) : PageModel
{
    public HomeContent HomeContent { get; private set; } = new();
    public bool CanViewUsefulLinks { get; private set; }

    public async Task OnGetAsync()
    {
        HomeContent = await homeContentService.GetContentAsync();
        CanViewUsefulLinks = await currentAccess.CanAsync(TeamHubModules.UsefulLinks);
    }
}
