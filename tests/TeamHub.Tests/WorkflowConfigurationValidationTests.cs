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

    [Fact]
    public void RejectsEscalationLevelsThatAreMissingRecipients()
    {
        var definitions = new List<WorkflowDefinitionDto>
        {
            new()
            {
                WorkflowKey = "ESCALATION",
                WorkflowName = "Escalation",
                Enabled = true,
                Steps =
                {
                    new WorkflowStepDefinitionDto
                    {
                        StepKey = "TASK",
                        StepName = "Task",
                        Owner = "owner@x.com",
                        ExpectedDurationHours = 1,
                        EscalationAfterHours = 8,
                        EscalationOwner = "lead@x.com; manager@x.com",
                        EscalationLevel2AfterHours = 4,
                        Required = true,
                        Enabled = true,
                        SortOrder = 1
                    }
                }
            }
        };

        var errors = WorkflowConfigurationService.ValidateDefinitions(definitions);

        Assert.Contains(errors, error => error.Contains("level 2 needs at least one recipient", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errors, error => error.Contains("must occur after", StringComparison.OrdinalIgnoreCase));
    }
}
