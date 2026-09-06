using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TeamHub.Web.Pages.Configuration;

[Authorize(Roles = "Admin")]
public sealed class IndexModel : PageModel
{
    public void OnGet()
    {
    }
}
