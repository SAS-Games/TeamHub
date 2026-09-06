using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TeamHub.FlowDesigner.Web.Pages.Flows;

public sealed class ViewModel : PageModel
{
    public IActionResult OnGet(Guid id) => RedirectToPage("/Flows/Edit", new { id });
}
