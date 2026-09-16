using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Milestones;
using TeamHub.Team;

namespace TeamHub.Web.Pages.Configuration;

[Authorize(Roles = "Admin")]
public sealed class MilestonesModel(
    IMilestoneConfigurationService configurationService,
    IMilestoneTrackerService milestoneTracker,
    IExcelSourceFileStore excelSourceFiles) : PageModel
{
    [BindProperty]
    public MilestoneSourceSettings Settings { get; set; } = new();

    [BindProperty]
    public IFormFile? WorkbookFile { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Settings = await configurationService.GetSettingsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PrepareUploadedWorkbookAsync(cancellationToken);
            await configurationService.SaveSettingsAsync(Settings, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }

        StatusMessage = "Milestone workbook configuration saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PrepareUploadedWorkbookAsync(cancellationToken);
            var result = await milestoneTracker.TestSourceAsync(Settings, cancellationToken);
            StatusMessage = $"Workbook test successful. TeamHub downloaded and parsed {result.MilestoneCount:N0} milestone row(s).";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or IOException
            or HttpRequestException)
        {
            ModelState.AddModelError(string.Empty, $"Workbook test failed: {exception.Message}");
        }

        return Page();
    }

    private async Task PrepareUploadedWorkbookAsync(CancellationToken cancellationToken)
    {
        if (Settings.SourceType != TeamHub.Excel.ExcelWorkbookSourceTypes.UploadedExcel
            || WorkbookFile is not { Length: > 0 })
            return;

        await using var upload = WorkbookFile.OpenReadStream();
        var stored = await excelSourceFiles.SaveAsync(
            upload,
            WorkbookFile.FileName,
            WorkbookFile.Length,
            cancellationToken);
        Settings.SourceUrl = stored.Reference;
        Settings.SourceDisplayName = stored.DisplayName;
    }
}
