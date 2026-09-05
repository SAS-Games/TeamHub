using ClosedXML.Excel;
using Microsoft.Extensions.Options;
using TeamHub.Application.Models;
using TeamHub.Infrastructure.Excel;
using TeamHub.Infrastructure.Options;
using TeamHub.Infrastructure.Services;

namespace TeamHub.Tests;

public class WorkflowConfigurationValidationTests
{
    [Fact]
    public void RejectsMissingDependency()
    {
        var defs = new List<WorkflowDefinitionDto>
        {
            new()
            {
                WorkflowKey = "NEW_HIRE",
                WorkflowName = "New Hire",
                Enabled = true,
                Steps =
                {
                    new WorkflowStepDefinitionDto
                    {
                        StepKey = "A",
                        StepName = "Step A",
                        Owner = "a@x.com",
                        ExpectedDurationHours = 1,
                        Required = true,
                        Enabled = true,
                        DependsOn = ["MISSING"],
                        SortOrder = 1
                    }
                }
            }
        };

        var errors = WorkflowConfigurationService.ValidateDefinitions(defs);
        Assert.Contains(errors, e => e.Contains("unknown dependency", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsCircularDependency()
    {
        var defs = new List<WorkflowDefinitionDto>
        {
            new()
            {
                WorkflowKey = "WF",
                WorkflowName = "Workflow",
                Enabled = true,
                Steps =
                {
                    new WorkflowStepDefinitionDto { StepKey = "A", StepName = "A", Owner = "a@x.com", ExpectedDurationHours = 1, Required = true, Enabled = true, DependsOn = ["B"], SortOrder = 1 },
                    new WorkflowStepDefinitionDto { StepKey = "B", StepName = "B", Owner = "b@x.com", ExpectedDurationHours = 1, Required = true, Enabled = true, DependsOn = ["A"], SortOrder = 2 }
                }
            }
        };

        var errors = WorkflowConfigurationService.ValidateDefinitions(defs);
        Assert.Contains(errors, e => e.Contains("circular", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoadDefinitionsAsync_SupportsSheetPerWorkflowFormat()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"workflow-sheet-per-workflow-{Guid.NewGuid():N}.xlsx");

        try
        {
            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("NEW_HIRE_ONBOARDING");
                worksheet.Cell("A1").Value = "StepKey";
                worksheet.Cell("B1").Value = "StepName";
                worksheet.Cell("C1").Value = "Owner";
                worksheet.Cell("D1").Value = "ExpectedDurationHours";
                worksheet.Cell("E1").Value = "Required";
                worksheet.Cell("F1").Value = "Enabled";
                worksheet.Cell("G1").Value = "SortOrder";
                worksheet.Cell("H1").Value = "DependsOn";

                worksheet.Cell("A2").Value = "WELCOME_EMAIL";
                worksheet.Cell("B2").Value = "Welcome Email";
                worksheet.Cell("C2").Value = "hr@company.com";
                worksheet.Cell("D2").Value = 2;
                worksheet.Cell("E2").Value = true;
                worksheet.Cell("F2").Value = true;
                worksheet.Cell("G2").Value = 1;
                worksheet.Cell("H2").Value = string.Empty;

                workbook.SaveAs(filePath);
            }

            var provider = new ExcelWorkflowDefinitionProvider(Options.Create(new WorkflowConfigurationOptions
            {
                ExcelPath = filePath
            }));

            var definitions = await provider.LoadDefinitionsAsync();

            var workflow = Assert.Single(definitions);
            Assert.Equal("NEW_HIRE_ONBOARDING", workflow.WorkflowKey);
            Assert.Equal("NEW_HIRE_ONBOARDING", workflow.WorkflowName);
            var step = Assert.Single(workflow.Steps);
            Assert.Equal("WELCOME_EMAIL", step.StepKey);
            Assert.Equal("Welcome Email", step.StepName);
            Assert.Equal("hr@company.com", step.Owner);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }
}
