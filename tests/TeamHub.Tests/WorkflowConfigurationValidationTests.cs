using TeamHub.Application.Models;
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
}
