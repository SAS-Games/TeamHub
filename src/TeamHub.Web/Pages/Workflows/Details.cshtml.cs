using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TeamHub.Domain.Entities;
using TeamHub.Infrastructure.Persistence;

namespace TeamHub.Web.Pages.Workflows;

public class DetailsModel(WorkflowDbContext dbContext) : PageModel
{
    public WorkflowInstance? Instance { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Instance = await dbContext.WorkflowInstances
            .Include(x => x.WorkflowDefinition)
            .Include(x => x.Steps.OrderBy(s => s.SortOrder))
            .FirstOrDefaultAsync(x => x.Id == id);

        if (Instance is null)
        {
            return NotFound();
        }

        return Page();
    }
}
