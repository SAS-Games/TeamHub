using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeamHub.AI.Contracts;
using TeamHub.AI.FlowDesigner.Application;
using TeamHub.AI.FlowDesigner.Contracts;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.Core.Validation;

namespace TeamHub.AI.FlowDesigner.Validation;

internal sealed record ParsedFlowDiagram(
    FlowDefinition Diagram,
    IReadOnlyList<FlowDefinition> ChildDiagrams,
    IReadOnlyList<FlowDesignerEvidenceReference> Evidence,
    IReadOnlyList<string> Warnings);

internal sealed class GeneratedFlowDiagramParser(
    IFlowValidator validator,
    IFlowHierarchyValidator hierarchyValidator)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public ParsedFlowDiagram Parse(
        AiStructuredGenerationResult generation,
        IReadOnlyCollection<string> sourceDocumentIds,
        int maximumDiagrams,
        int maximumHierarchyDepth)
    {
        GeneratedHierarchyResponse response;
        try
        {
            response = JsonSerializer.Deserialize<GeneratedHierarchyResponse>(generation.Content, SerializerOptions)
                ?? throw new JsonException("The response was empty.");
        }
        catch (JsonException exception)
        {
            throw new FlowDiagramGenerationException(
                "The AI model returned a response that does not match the required Flow Designer hierarchy schema.",
                exception);
        }

        ValidateIdentifier(response.RootDiagramKey, "Root diagram key");
        if (response.Diagrams is null || response.Diagrams.Count is < 1 || response.Diagrams.Count > maximumDiagrams)
        {
            throw new FlowDiagramGenerationException(
                $"The generated hierarchy must contain between 1 and {maximumDiagrams} diagrams.");
        }

        var diagramKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var diagram in response.Diagrams)
        {
            ValidateIdentifier(diagram.Key, "Diagram key");
            diagram.Key = diagram.Key.Trim();
            if (!diagramKeys.Add(diagram.Key))
            {
                throw new FlowDiagramGenerationException($"Generated diagram key '{diagram.Key}' is duplicated.");
            }
        }
        var rootKey = response.RootDiagramKey.Trim();
        if (!diagramKeys.Contains(rootKey))
        {
            throw new FlowDiagramGenerationException($"Root diagram key '{rootKey}' is not present in the hierarchy.");
        }

        var diagramIds = response.Diagrams.ToDictionary(
            diagram => diagram.Key,
            _ => Guid.NewGuid(),
            StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var flowsByKey = new Dictionary<string, FlowDefinition>(StringComparer.Ordinal);
        foreach (var diagram in response.Diagrams)
        {
            flowsByKey.Add(diagram.Key, BuildFlow(diagram, diagramIds, generation, now));
        }

        var hierarchy = new FlowDiagramTemplateBundle
        {
            RootFlowId = diagramIds[rootKey],
            Flows = response.Diagrams.Select(diagram => flowsByKey[diagram.Key]).ToList()
        };
        var hierarchyValidation = hierarchyValidator.Validate(
            hierarchy,
            maximumDiagrams,
            maximumHierarchyDepth);
        if (!hierarchyValidation.IsValid)
        {
            throw new FlowDiagramGenerationException(
                "The AI proposal is not a valid Flow Designer hierarchy: "
                + string.Join(" ", hierarchyValidation.Issues.Select(issue => issue.Message)));
        }

        var warnings = (response.Warnings ?? [])
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Select(warning => warning.Trim())
            .ToList();
        foreach (var pair in flowsByKey)
        {
            var validation = validator.Validate(pair.Value);
            var errors = validation.Issues.Where(issue => issue.Severity == ValidationSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                throw new FlowDiagramGenerationException(
                    $"Generated diagram '{pair.Value.Name}' is not a valid Flow Designer diagram: "
                    + string.Join(" ", errors.Select(error => error.Message)));
            }
            warnings.AddRange(validation.Issues
                .Where(issue => issue.Severity == ValidationSeverity.Warning)
                .Select(issue => $"{pair.Value.Name}: {issue.Message}"));
        }

        var evidence = ValidateEvidence(
            response.Evidence,
            sourceDocumentIds,
            response.Diagrams,
            flowsByKey);
        var referencedElements = evidence
            .Select(item => $"{item.DiagramKey}\u001f{item.ElementId}")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var diagram in response.Diagrams)
        {
            var flow = flowsByKey[diagram.Key];
            foreach (var node in flow.Nodes.Where(node => node.Type is not NodeType.Start
                                                          and not NodeType.End
                                                          and not NodeType.Section
                                                          and not NodeType.Annotation))
            {
                if (!referencedElements.Contains($"{diagram.Key}\u001f{node.Id}"))
                {
                    warnings.Add($"{flow.Name}: generated node '{node.Title}' has no source evidence reference.");
                }
            }
        }

        var root = flowsByKey[rootKey];
        var children = response.Diagrams
            .Where(diagram => diagram.Key != rootKey)
            .Select(diagram => flowsByKey[diagram.Key])
            .ToList();
        return new ParsedFlowDiagram(
            root,
            children,
            evidence,
            warnings.Distinct(StringComparer.Ordinal).ToList());
    }

    private static FlowDefinition BuildFlow(
        GeneratedDiagram diagram,
        IReadOnlyDictionary<string, Guid> diagramIds,
        AiStructuredGenerationResult generation,
        DateTimeOffset now)
    {
        ValidateText(diagram.Name, $"Name for diagram '{diagram.Key}'", 200, required: true);
        ValidateText(diagram.Description, $"Description for diagram '{diagram.Key}'", 2_000);
        if (!Enum.TryParse<DiagramType>(diagram.DiagramType, true, out var diagramType))
        {
            throw new FlowDiagramGenerationException(
                $"Unsupported diagram type '{diagram.DiagramType}' in diagram '{diagram.Key}'.");
        }
        if (diagram.Nodes is null || diagram.Nodes.Count is < 1 or > 100)
        {
            throw new FlowDiagramGenerationException(
                $"Generated diagram '{diagram.Key}' must contain between 1 and 100 nodes.");
        }
        if (diagram.Connections is null || diagram.Connections.Count > 200)
        {
            throw new FlowDiagramGenerationException(
                $"Generated diagram '{diagram.Key}' cannot contain more than 200 connections.");
        }

        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var nodeTypes = new Dictionary<string, NodeType>(StringComparer.Ordinal);
        foreach (var node in diagram.Nodes)
        {
            ValidateIdentifier(node.Id, $"Node ID in diagram '{diagram.Key}'");
            node.Id = node.Id.Trim();
            if (!nodeIds.Add(node.Id))
            {
                throw new FlowDiagramGenerationException(
                    $"Generated node ID '{node.Id}' is duplicated in diagram '{diagram.Key}'.");
            }
            if (!Enum.TryParse<NodeType>(node.Type, true, out var nodeType))
            {
                throw new FlowDiagramGenerationException($"Unsupported node type '{node.Type}'.");
            }
            nodeTypes.Add(node.Id, nodeType);
            ValidateText(node.Title, $"Title for node '{node.Id}'", 200, required: true);
            ValidateText(node.Description, $"Description for node '{node.Id}'", 2_000);
            ValidateText(node.Notes, $"Notes for node '{node.Id}'", 1_000);

            var childKey = node.ChildDiagramKey?.Trim() ?? string.Empty;
            if (childKey.Length > 0)
            {
                ValidateIdentifier(childKey, $"Child diagram key for node '{node.Id}'");
                if (nodeType != NodeType.Subprocess)
                {
                    throw new FlowDiagramGenerationException(
                        $"Only a Subprocess node can open child diagram '{childKey}'.");
                }
                if (!diagramIds.ContainsKey(childKey))
                {
                    throw new FlowDiagramGenerationException(
                        $"Node '{node.Id}' links to missing child diagram '{childKey}'.");
                }
            }
        }

        var connectionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in diagram.Connections)
        {
            ValidateIdentifier(connection.Id, $"Connection ID in diagram '{diagram.Key}'");
            connection.Id = connection.Id.Trim();
            if (!connectionIds.Add(connection.Id))
            {
                throw new FlowDiagramGenerationException(
                    $"Generated connection ID '{connection.Id}' is duplicated in diagram '{diagram.Key}'.");
            }
            ValidateIdentifier(connection.SourceNodeId, $"Source node ID for connection '{connection.Id}'");
            ValidateIdentifier(connection.TargetNodeId, $"Target node ID for connection '{connection.Id}'");
            connection.SourceNodeId = connection.SourceNodeId.Trim();
            connection.TargetNodeId = connection.TargetNodeId.Trim();
            ValidatePin(connection.OutputPin, "output", connection.Id);
            ValidatePin(connection.InputPin, "input", connection.Id);
            ValidateText(connection.Label, $"Label for connection '{connection.Id}'", 200);

            if (nodeTypes.TryGetValue(connection.SourceNodeId, out var sourceType) && !CanHaveOutput(sourceType))
            {
                throw new FlowDiagramGenerationException(
                    $"Connection '{connection.Id}' cannot start at {sourceType} node '{connection.SourceNodeId}'.");
            }
            if (nodeTypes.TryGetValue(connection.TargetNodeId, out var targetType) && !CanHaveInput(targetType))
            {
                throw new FlowDiagramGenerationException(
                    $"Connection '{connection.Id}' cannot end at {targetType} node '{connection.TargetNodeId}'.");
            }
        }

        var flow = new FlowDefinition
        {
            Id = diagramIds[diagram.Key],
            Name = diagram.Name.Trim(),
            Description = diagram.Description?.Trim() ?? string.Empty,
            DiagramType = diagramType,
            CreatedAt = now,
            UpdatedAt = now,
            IsShared = false,
            Version = 1,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ai.generated"] = "true",
                ["ai.schemaVersion"] = FlowDiagramPromptBuilder.SchemaVersion,
                ["ai.diagramKey"] = diagram.Key,
                ["ai.provider"] = generation.Provider,
                ["ai.model"] = generation.Model
            }
        };
        if (!string.IsNullOrWhiteSpace(generation.RequestId))
        {
            flow.Metadata["ai.requestId"] = generation.RequestId;
        }

        var maxInputPins = diagram.Connections
            .GroupBy(connection => connection.TargetNodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(connection => connection.InputPin), StringComparer.Ordinal);
        var maxOutputPins = diagram.Connections
            .GroupBy(connection => connection.SourceNodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(connection => connection.OutputPin), StringComparer.Ordinal);
        flow.Nodes = diagram.Nodes.Select(node =>
        {
            var customProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["portLayout"] = "top-bottom"
            };
            if (!string.IsNullOrWhiteSpace(node.Notes)) customProperties["notes"] = node.Notes.Trim();
            if (maxInputPins.TryGetValue(node.Id, out var inputPins))
            {
                customProperties["inputPins"] = inputPins.ToString(CultureInfo.InvariantCulture);
            }
            if (maxOutputPins.TryGetValue(node.Id, out var outputPins))
            {
                customProperties["outputPins"] = outputPins.ToString(CultureInfo.InvariantCulture);
            }

            var childKey = node.ChildDiagramKey?.Trim() ?? string.Empty;
            return new FlowNode
            {
                Id = node.Id,
                Type = nodeTypes[node.Id],
                Title = node.Title.Trim(),
                Description = node.Description?.Trim() ?? string.Empty,
                ChildFlowId = childKey.Length == 0 ? null : diagramIds[childKey],
                CustomProperties = customProperties
            };
        }).ToList();
        flow.Connections = diagram.Connections.Select(connection => new FlowConnection
        {
            Id = connection.Id,
            SourceNodeId = connection.SourceNodeId,
            TargetNodeId = connection.TargetNodeId,
            SourcePort = $"output_{connection.OutputPin}",
            TargetPort = $"input_{connection.InputPin}",
            Label = string.IsNullOrWhiteSpace(connection.Label) ? null : connection.Label.Trim()
        }).ToList();
        ApplyFlowDesignerLayout(flow);
        return flow;
    }

    private static IReadOnlyList<FlowDesignerEvidenceReference> ValidateEvidence(
        IReadOnlyList<GeneratedEvidence>? generatedEvidence,
        IReadOnlyCollection<string> sourceDocumentIds,
        IReadOnlyList<GeneratedDiagram> diagrams,
        IReadOnlyDictionary<string, FlowDefinition> flowsByKey)
    {
        var sourceIds = sourceDocumentIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var diagramsByKey = diagrams.ToDictionary(diagram => diagram.Key, StringComparer.Ordinal);
        var evidence = new List<FlowDesignerEvidenceReference>();
        foreach (var item in generatedEvidence ?? [])
        {
            ValidateIdentifier(item.DiagramKey, "Evidence diagram key");
            ValidateIdentifier(item.ElementId, "Evidence element ID");
            ValidateIdentifier(item.SourceDocumentId, "Evidence source document ID");
            ValidateText(item.Location, "Evidence location", 300);
            ValidateText(item.Explanation, "Evidence explanation", 500, required: true);

            var diagramKey = item.DiagramKey.Trim();
            if (!diagramsByKey.ContainsKey(diagramKey))
            {
                throw new FlowDiagramGenerationException(
                    $"Evidence references unknown diagram '{diagramKey}'.");
            }
            var elementId = item.ElementId.Trim();
            var flow = flowsByKey[diagramKey];
            if (flow.Nodes.All(node => node.Id != elementId)
                && flow.Connections.All(connection => connection.Id != elementId))
            {
                throw new FlowDiagramGenerationException(
                    $"Evidence references unknown element '{elementId}' in diagram '{diagramKey}'.");
            }
            var sourceId = item.SourceDocumentId.Trim();
            if (!sourceIds.Contains(sourceId))
            {
                throw new FlowDiagramGenerationException(
                    $"Evidence references unknown source document '{sourceId}'.");
            }
            evidence.Add(new FlowDesignerEvidenceReference(
                elementId,
                sourceId,
                string.IsNullOrWhiteSpace(item.Location) ? null : item.Location.Trim(),
                item.Explanation.Trim(),
                diagramKey));
        }
        return evidence;
    }

    private static void ApplyFlowDesignerLayout(FlowDefinition flow)
    {
        var levels = flow.Nodes.ToDictionary(node => node.Id, _ => 0, StringComparer.Ordinal);
        var incoming = flow.Nodes.ToDictionary(node => node.Id, _ => 0, StringComparer.Ordinal);
        var outgoing = flow.Nodes.ToDictionary(node => node.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var connection in flow.Connections)
        {
            if (!incoming.ContainsKey(connection.TargetNodeId) || !outgoing.ContainsKey(connection.SourceNodeId)) continue;
            incoming[connection.TargetNodeId]++;
            outgoing[connection.SourceNodeId].Add(connection.TargetNodeId);
        }

        var queue = new Queue<string>(flow.Nodes.Where(node => incoming[node.Id] == 0).Select(node => node.Id));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out var nodeId))
        {
            if (!visited.Add(nodeId)) continue;
            foreach (var targetId in outgoing[nodeId])
            {
                levels[targetId] = Math.Max(levels[targetId], levels[nodeId] + 1);
                incoming[targetId]--;
                if (incoming[targetId] == 0) queue.Enqueue(targetId);
            }
        }
        var cycleLevel = levels.Values.DefaultIfEmpty(0).Max() + 1;
        foreach (var node in flow.Nodes.Where(node => !visited.Contains(node.Id))) levels[node.Id] = cycleLevel;
        foreach (var level in flow.Nodes.GroupBy(node => levels[node.Id]).OrderBy(group => group.Key))
        {
            var index = 0;
            foreach (var node in level)
            {
                node.X = 120 + (index * 360);
                node.Y = 100 + (level.Key * 220);
                index++;
            }
        }
    }

    private static void ValidateIdentifier(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 100)
        {
            throw new FlowDiagramGenerationException($"{fieldName} must contain 1 to 100 characters.");
        }
    }

    private static void ValidateText(string? value, string fieldName, int maximumLength, bool required = false)
    {
        var length = value?.Trim().Length ?? 0;
        if ((required && length == 0) || length > maximumLength)
        {
            var range = required ? $"1 to {maximumLength:N0}" : $"no more than {maximumLength:N0}";
            throw new FlowDiagramGenerationException($"{fieldName} must contain {range} characters.");
        }
    }

    private static void ValidatePin(int pin, string kind, string connectionId)
    {
        if (pin is < 1 or > 6)
        {
            throw new FlowDiagramGenerationException(
                $"Connection '{connectionId}' uses an invalid {kind} pin. Flow Designer supports pins 1 through 6.");
        }
    }

    private static bool CanHaveInput(NodeType type) => type is not NodeType.Start and not NodeType.Annotation;
    private static bool CanHaveOutput(NodeType type) => type is not NodeType.End and not NodeType.Annotation;

    private sealed class GeneratedHierarchyResponse
    {
        public string RootDiagramKey { get; set; } = string.Empty;
        public List<GeneratedDiagram> Diagrams { get; set; } = [];
        public List<GeneratedEvidence> Evidence { get; set; } = [];
        public List<string> Warnings { get; set; } = [];
    }

    private sealed class GeneratedDiagram
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string DiagramType { get; set; } = string.Empty;
        public List<GeneratedNode> Nodes { get; set; } = [];
        public List<GeneratedConnection> Connections { get; set; } = [];
    }

    private sealed class GeneratedNode
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public string ChildDiagramKey { get; set; } = string.Empty;
    }

    private sealed class GeneratedConnection
    {
        public string Id { get; set; } = string.Empty;
        public string SourceNodeId { get; set; } = string.Empty;
        public string TargetNodeId { get; set; } = string.Empty;
        public int OutputPin { get; set; }
        public int InputPin { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    private sealed class GeneratedEvidence
    {
        public string DiagramKey { get; set; } = string.Empty;
        public string ElementId { get; set; } = string.Empty;
        public string SourceDocumentId { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string Explanation { get; set; } = string.Empty;
    }
}