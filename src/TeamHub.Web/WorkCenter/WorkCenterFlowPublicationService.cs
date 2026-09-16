using System.Globalization;
using System.Security.Claims;
using TeamHub.Application.Interfaces;
using TeamHub.Application.Models;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;
using TeamHub.Authentication;

namespace TeamHub.Web.WorkCenter;

public sealed class WorkCenterFlowPublicationService(
    IWorkflowConfigurationService workflowConfiguration,
    IHttpContextAccessor httpContextAccessor) : IFlowPublicationService
{
    private static readonly HashSet<NodeType> SupportedNodeTypes =
    [
        NodeType.Start,
        NodeType.End,
        NodeType.Activity,
        NodeType.ParallelGateway,
        NodeType.Section,
        NodeType.Annotation
    ];

    public bool CanPublish(FlowDefinition flow) =>
        flow.DiagramType == DiagramType.WorkCenterWorkflow
        && httpContextAccessor.HttpContext?.User.IsInRole(TeamHubUserTypes.Admin) == true;

    public async Task<FlowPublicationResult> PublishAsync(
        FlowDefinition flow,
        CancellationToken cancellationToken = default)
    {
        if (!CanPublish(flow))
        {
            throw new UnauthorizedAccessException("Full Work Center access is required to publish workflows.");
        }

        var errors = Validate(flow);
        if (errors.Count > 0)
        {
            return FlowPublicationResult.Invalid(errors);
        }

        var draft = BuildDraft(flow);
        var draftId = await workflowConfiguration.SaveDraftAsync(draft, cancellationToken);
        var actor = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.Name)
            ?? httpContextAccessor.HttpContext?.User.Identity?.Name
            ?? "admin@local";
        var result = await workflowConfiguration.PublishDraftAsync(draftId, actor, cancellationToken);

        return result.Success
            ? FlowPublicationResult.Published($"Published {flow.Name}: {string.Join("; ", result.PublishedWorkflowSummaries)}")
            : FlowPublicationResult.Invalid(result.Errors);
    }

    private static List<string> Validate(FlowDefinition flow)
    {
        var errors = new List<string>();
        if (flow.Version <= 0)
        {
            errors.Add("Save the workflow before publishing it.");
        }

        foreach (var node in flow.Nodes.Where(node => !SupportedNodeTypes.Contains(node.Type)))
        {
            errors.Add($"'{node.Title}' uses {node.Type}, which Work Center cannot execute yet.");
        }

        var starts = flow.Nodes.Where(node => node.Type == NodeType.Start).ToList();
        if (starts.Count != 1)
        {
            errors.Add($"A Work Center workflow needs exactly one Start node; this diagram has {starts.Count}.");
        }

        var ends = flow.Nodes.Where(node => node.Type == NodeType.End).ToList();
        if (ends.Count == 0)
        {
            errors.Add("A Work Center workflow needs at least one End node.");
        }

        var tasks = flow.Nodes.Where(IsTask).ToList();
        if (tasks.Count == 0)
        {
            errors.Add("Add at least one Task to the workflow.");
        }

        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(Get(task.CustomProperties, "owner")))
            {
                errors.Add($"Task '{task.Title}' needs an assignee.");
            }

            ValidateNumber(task, "expectedDurationHours", "Expected duration", errors);
            ValidateNumber(task, "reminderAfterHours", "First reminder", errors);
            ValidateNumber(task, "reminderRepeatHours", "Reminder repeat", errors);
            ValidateNumber(task, "escalationAfterHours", "Escalation delay", errors);

            if (!string.IsNullOrWhiteSpace(Get(task.CustomProperties, "escalationAfterHours"))
                && string.IsNullOrWhiteSpace(Get(task.CustomProperties, "escalationOwner")))
            {
                errors.Add($"Task '{task.Title}' needs an escalation owner when escalation is enabled.");
            }
        }

        var visualNodeIds = flow.Nodes
            .Where(node => node.Type is NodeType.Section or NodeType.Annotation)
            .Select(node => node.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (flow.Connections.Any(connection =>
                visualNodeIds.Contains(connection.SourceNodeId) || visualNodeIds.Contains(connection.TargetNodeId)))
        {
            errors.Add("Section and Annotation nodes are visual only and cannot be part of the executable path.");
        }

        var executableIds = flow.Nodes
            .Where(node => node.Type is NodeType.Start or NodeType.End or NodeType.Activity or NodeType.ParallelGateway)
            .Select(node => node.Id)
            .ToHashSet(StringComparer.Ordinal);
        var executableConnections = flow.Connections
            .Where(connection => executableIds.Contains(connection.SourceNodeId) && executableIds.Contains(connection.TargetNodeId))
            .ToList();

        if (HasCycle(executableIds, executableConnections))
        {
            errors.Add("Work Center workflows cannot contain loops.");
        }

        if (starts.Count == 1)
        {
            var reachable = ReachableFrom(starts[0].Id, executableConnections);
            foreach (var task in tasks.Where(task => !reachable.Contains(task.Id)))
            {
                errors.Add($"Task '{task.Title}' is not reachable from Start.");
            }
            if (ends.Count > 0 && ends.All(end => !reachable.Contains(end.Id)))
            {
                errors.Add("No End node is reachable from Start.");
            }
        }

        if (starts.Any(start => executableConnections.Any(connection => connection.TargetNodeId == start.Id)))
        {
            errors.Add("A Start node cannot have an incoming connection.");
        }
        if (ends.Any(end => executableConnections.Any(connection => connection.SourceNodeId == end.Id)))
        {
            errors.Add("An End node cannot have an outgoing connection.");
        }

        return errors.Distinct(StringComparer.Ordinal).ToList();
    }

    private static WorkflowDraftDto BuildDraft(FlowDefinition flow)
    {
        var orderedTasks = flow.Nodes
            .Where(IsTask)
            .OrderBy(node => node.Y)
            .ThenBy(node => node.X)
            .ToList();
        var taskIds = orderedTasks.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var nodeById = flow.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var incoming = flow.Connections
            .GroupBy(connection => connection.TargetNodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(connection => connection.SourceNodeId).ToList(), StringComparer.Ordinal);
        var stepKeyByNode = orderedTasks.ToDictionary(
            node => node.Id,
            node => NormalizeKey(Get(node.CustomProperties, "stepKey") ?? node.Title),
            StringComparer.Ordinal);

        return new WorkflowDraftDto
        {
            WorkflowKey = NormalizeKey(Get(flow.Metadata, "workflowKey") ?? flow.Name),
            WorkflowName = flow.Name,
            Description = flow.Description,
            Enabled = GetBool(flow.Metadata, "enabled", true),
            Steps = orderedTasks.Select((node, index) => new WorkflowDraftStepDto
            {
                StepKey = stepKeyByNode[node.Id],
                StepName = node.Title,
                Description = string.IsNullOrWhiteSpace(node.Description) ? null : node.Description,
                OwnerType = Get(node.CustomProperties, "ownerType") ?? "User",
                Owner = Get(node.CustomProperties, "owner") ?? string.Empty,
                ExpectedDurationHours = GetDouble(node.CustomProperties, "expectedDurationHours") ?? 24,
                DependsOnCsv = string.Join(", ", FindUpstreamTasks(node.Id, incoming, nodeById, taskIds)
                    .Select(id => stepKeyByNode[id])
                    .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)),
                ReminderAfterHours = GetDouble(node.CustomProperties, "reminderAfterHours"),
                ReminderRepeatHours = GetDouble(node.CustomProperties, "reminderRepeatHours"),
                EscalationAfterHours = GetDouble(node.CustomProperties, "escalationAfterHours"),
                EscalationOwner = Get(node.CustomProperties, "escalationOwner"),
                Required = GetBool(node.CustomProperties, "required", true),
                Enabled = GetBool(node.CustomProperties, "enabled", true),
                SortOrder = index + 1
            }).ToList()
        };
    }

    private static HashSet<string> FindUpstreamTasks(
        string nodeId,
        IReadOnlyDictionary<string, List<string>> incoming,
        IReadOnlyDictionary<string, FlowNode> nodeById,
        IReadOnlySet<string> taskIds)
    {
        var tasks = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(incoming.GetValueOrDefault(nodeId) ?? []);

        while (pending.TryPop(out var sourceId))
        {
            if (!visited.Add(sourceId) || !nodeById.ContainsKey(sourceId)) continue;
            if (taskIds.Contains(sourceId))
            {
                tasks.Add(sourceId);
                continue;
            }
            foreach (var upstreamId in incoming.GetValueOrDefault(sourceId) ?? []) pending.Push(upstreamId);
        }

        return tasks;
    }

    private static bool HasCycle(IReadOnlySet<string> nodeIds, IReadOnlyList<FlowConnection> connections)
    {
        var outgoing = connections
            .GroupBy(connection => connection.SourceNodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(connection => connection.TargetNodeId).ToList(), StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        bool Visit(string nodeId)
        {
            if (visited.Contains(nodeId)) return false;
            if (!visiting.Add(nodeId)) return true;
            foreach (var targetId in outgoing.GetValueOrDefault(nodeId) ?? [])
            {
                if (Visit(targetId)) return true;
            }
            visiting.Remove(nodeId);
            visited.Add(nodeId);
            return false;
        }

        return nodeIds.Any(nodeId => Visit(nodeId));
    }

    private static HashSet<string> ReachableFrom(string startId, IReadOnlyList<FlowConnection> connections)
    {
        var outgoing = connections
            .GroupBy(connection => connection.SourceNodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(connection => connection.TargetNodeId).ToList(), StringComparer.Ordinal);
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(startId);
        while (pending.TryPop(out var nodeId))
        {
            if (!reachable.Add(nodeId)) continue;
            foreach (var targetId in outgoing.GetValueOrDefault(nodeId) ?? []) pending.Push(targetId);
        }
        return reachable;
    }

    private static void ValidateNumber(FlowNode node, string property, string label, ICollection<string> errors)
    {
        var value = Get(node.CustomProperties, property);
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            errors.Add($"Task '{node.Title}': {label} must be zero or greater.");
        }
    }

    private static bool IsTask(FlowNode node) => node.Type == NodeType.Activity;

    private static string? Get(IReadOnlyDictionary<string, string>? properties, string key) =>
        properties is not null && properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static bool GetBool(IReadOnlyDictionary<string, string>? properties, string key, bool defaultValue) =>
        bool.TryParse(Get(properties, key), out var value) ? value : defaultValue;

    private static double? GetDouble(IReadOnlyDictionary<string, string>? properties, string key) =>
        double.TryParse(Get(properties, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string NormalizeKey(string value) =>
        string.Join('_', value.Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
