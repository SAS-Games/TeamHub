using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.Core.Validation;

namespace TeamHub.FlowDesigner.Core.Contracts;

public interface IFlowValidator
{
    FlowValidationResult Validate(FlowDefinition flow);
}
