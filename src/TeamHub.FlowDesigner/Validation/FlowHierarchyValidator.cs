using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.Core.Validation;

namespace TeamHub.FlowDesigner.Validation;

public sealed class FlowHierarchyValidator : IFlowHierarchyValidator
{
    public FlowValidationResult Validate(
        FlowDiagramTemplateBundle hierarchy,
        int maximumDiagrams = 25,
        int maximumDepth = 6)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        var issues = new List<FlowValidationIssue>();
        if (maximumDiagrams < 1) throw new ArgumentOutOfRangeException(nameof(maximumDiagrams));
        if (maximumDepth < 1) throw new ArgumentOutOfRangeException(nameof(maximumDepth));

        if (hierarchy.Flows.Count == 0)
        {
            issues.Add(new("hierarchy-empty", "The diagram hierarchy is empty.", ValidationSeverity.Error));
            return new FlowValidationResult { Issues = issues };
        }

        if (hierarchy.Flows.Count > maximumDiagrams)
        {
            issues.Add(new(
                "hierarchy-size",
                $"The hierarchy has {hierarchy.Flows.Count} diagrams; the limit is {maximumDiagrams}.",
                ValidationSeverity.Error));
        }

        var groups = hierarchy.Flows.GroupBy(flow => flow.Id).ToList();
        foreach (var duplicate in groups.Where(group => group.Key == Guid.Empty || group.Count() > 1))
        {
            issues.Add(new(
                "hierarchy-diagram-id",
                duplicate.Key == Guid.Empty
                    ? "Every diagram in the hierarchy must have an ID."
                    : $"Diagram ID '{duplicate.Key}' is duplicated.",
                ValidationSeverity.Error,
                duplicate.Key.ToString()));
        }

        var byId = groups.Where(group => group.Key != Guid.Empty)
            .ToDictionary(group => group.Key, group => group.First());
        if (hierarchy.RootFlowId == Guid.Empty || !byId.ContainsKey(hierarchy.RootFlowId))
        {
            issues.Add(new("hierarchy-root", "The hierarchy does not contain a valid root diagram.", ValidationSeverity.Error));
            return new FlowValidationResult { Issues = issues };
        }

        var parents = byId.Keys.ToDictionary(id => id, _ => new List<Guid>());
        foreach (var parent in hierarchy.Flows)
        {
            foreach (var childId in parent.Nodes
                         .Where(node => node.ChildFlowId.HasValue)
                         .Select(node => node.ChildFlowId!.Value))
            {
                if (!byId.ContainsKey(childId))
                {
                    issues.Add(new(
                        "hierarchy-missing-child",
                        $"'{parent.Name}' links to a child diagram that is not included in the hierarchy.",
                        ValidationSeverity.Error,
                        parent.Id.ToString()));
                    continue;
                }
                parents[childId].Add(parent.Id);
            }
        }

        if (parents[hierarchy.RootFlowId].Count > 0)
        {
            issues.Add(new(
                "hierarchy-root-parent",
                "The root diagram cannot also be a child diagram.",
                ValidationSeverity.Error,
                hierarchy.RootFlowId.ToString()));
        }

        foreach (var child in hierarchy.Flows.Where(flow => flow.Id != hierarchy.RootFlowId))
        {
            var distinctParents = parents[child.Id].Distinct().ToList();
            if (distinctParents.Count == 0)
            {
                issues.Add(new(
                    "hierarchy-orphan",
                    $"Child diagram '{child.Name}' is not linked from a parent.",
                    ValidationSeverity.Error,
                    child.Id.ToString()));
            }
            else if (distinctParents.Count > 1 || parents[child.Id].Count > 1)
            {
                issues.Add(new(
                    "hierarchy-multiple-parents",
                    $"Child diagram '{child.Name}' must be linked exactly once from one parent.",
                    ValidationSeverity.Error,
                    child.Id.ToString()));
            }
        }

        var visited = new HashSet<Guid>();
        var active = new HashSet<Guid>();
        void Visit(Guid id, int depth)
        {
            if (depth > maximumDepth)
            {
                issues.Add(new(
                    "hierarchy-depth",
                    $"The diagram hierarchy exceeds the maximum depth of {maximumDepth}.",
                    ValidationSeverity.Error,
                    id.ToString()));
                return;
            }
            if (!active.Add(id))
            {
                issues.Add(new(
                    "hierarchy-cycle",
                    "The diagram hierarchy contains a parent/child cycle.",
                    ValidationSeverity.Error,
                    id.ToString()));
                return;
            }
            if (!visited.Add(id))
            {
                active.Remove(id);
                return;
            }

            foreach (var childId in byId[id].Nodes
                         .Where(node => node.ChildFlowId.HasValue && byId.ContainsKey(node.ChildFlowId.Value))
                         .Select(node => node.ChildFlowId!.Value))
            {
                Visit(childId, depth + 1);
            }
            active.Remove(id);
        }

        Visit(hierarchy.RootFlowId, 1);
        if (visited.Count != byId.Count)
        {
            issues.Add(new(
                "hierarchy-unreachable",
                "Every child diagram must be reachable from the root diagram.",
                ValidationSeverity.Error));
        }

        return new FlowValidationResult
        {
            Issues = issues.DistinctBy(issue => new { issue.Code, issue.Message, issue.ElementId }).ToList()
        };
    }
}