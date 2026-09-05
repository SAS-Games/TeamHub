using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Web.Home;

namespace TeamHub.Web.Pages;

public class IndexModel(IHomeContentService homeContentService) : PageModel
{
    public HomeContent HomeContent { get; private set; } = new();

    public async Task OnGetAsync()
    {
        HomeContent = await homeContentService.GetContentAsync();
    }
}
