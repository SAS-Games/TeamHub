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
    IReadOnlyList<FlowDesignerEvidenceReference> Evidence,
    IReadOnlyList<string> Warnings);

internal sealed class GeneratedFlowDiagramParser(IFlowValidator validator)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public ParsedFlowDiagram Parse(
        AiStructuredGenerationResult generation,
        IReadOnlyCollection<string> sourceDocumentIds)
    {
        GeneratedDiagramResponse response;
        try
        {
            response = JsonSerializer.Deserialize<GeneratedDiagramResponse>(generation.Content, SerializerOptions)
                ?? throw new JsonException("The response was empty.");
        }
        catch (JsonException exception)
        {
            throw new FlowDiagramGenerationException(
                "The AI model returned a diagram response that does not match the required Flow Designer schema.",
                exception);
        }

        ValidateText(response.Name, "Diagram name", 200, required: true);
        ValidateText(response.Description, "Diagram description", 2_000);
        if (!Enum.TryParse<DiagramType>(response.DiagramType, true, out var diagramType))
        {
            throw new FlowDiagramGenerationException($"Unsupported diagram type '{response.DiagramType}'.");
        }

        if (response.Nodes is null || response.Nodes.Count is < 1 or > 100)
        {
            throw new FlowDiagramGenerationException("The generated diagram must contain between 1 and 100 nodes.");
        }

        if (response.Connections is null || response.Connections.Count > 200)
        {
            throw new FlowDiagramGenerationException("The generated diagram cannot contain more than 200 connections.");
        }

        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var nodeTypes = new Dictionary<string, NodeType>(StringComparer.Ordinal);
        foreach (var node in response.Nodes)
        {
            ValidateIdentifier(node.Id, "Node ID");
            if (!nodeIds.Add(node.Id.Trim()))
            {
                throw new FlowDiagramGenerationException($"Generated node ID '{node.Id}' is duplicated.");
            }

            if (!Enum.TryParse<NodeType>(node.Type, true, out var nodeType))
            {
                throw new FlowDiagramGenerationException($"Unsupported node type '{node.Type}'.");
            }

            nodeTypes.Add(node.Id.Trim(), nodeType);
            ValidateText(node.Title, $"Title for node '{node.Id}'", 200, required: true);
            ValidateText(node.Description, $"Description for node '{node.Id}'", 2_000);
            ValidateText(node.Notes, $"Notes for node '{node.Id}'", 1_000);
        }

        var connectionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in response.Connections)
        {
            ValidateIdentifier(connection.Id, "Connection ID");
            if (!connectionIds.Add(connection.Id.Trim()))
            {
                throw new FlowDiagramGenerationException($"Generated connection ID '{connection.Id}' is duplicated.");
            }

            ValidateIdentifier(connection.SourceNodeId, $"Source node ID for connection '{connection.Id}'");
            ValidateIdentifier(connection.TargetNodeId, $"Target node ID for connection '{connection.Id}'");
            ValidatePin(connection.OutputPin, "output", connection.Id);
            ValidatePin(connection.InputPin, "input", connection.Id);
            ValidateText(connection.Label, $"Label for connection '{connection.Id}'", 200);

            var sourceId = connection.SourceNodeId.Trim();
            var targetId = connection.TargetNodeId.Trim();
            if (nodeTypes.TryGetValue(sourceId, out var sourceType) && !CanHaveOutput(sourceType))
            {
                throw new FlowDiagramGenerationException(
                    $"Connection '{connection.Id}' cannot start at {sourceType} node '{sourceId}'.");
            }

            if (nodeTypes.TryGetValue(targetId, out var targetType) && !CanHaveInput(targetType))
            {
                throw new FlowDiagramGenerationException(
                    $"Connection '{connection.Id}' cannot end at {targetType} node '{targetId}'.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        var flow = new FlowDefinition
        {
            Id = Guid.NewGuid(),
            Name = response.Name.Trim(),
            Description = response.Description?.Trim() ?? string.Empty,
            DiagramType = diagramType,
            CreatedAt = now,
            UpdatedAt = now,
            IsShared = false,
            Version = 1,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ai.generated"] = "true",
                ["ai.schemaVersion"] = FlowDiagramPromptBuilder.SchemaVersion,
                ["ai.provider"] = generation.Provider,
                ["ai.model"] = generation.Model
            }
        };
        if (!string.IsNullOrWhiteSpace(generation.RequestId))
        {
            flow.Metadata["ai.requestId"] = generation.RequestId;
        }

        var maxInputPins = response.Connections
            .GroupBy(connection => connection.TargetNodeId.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(connection => connection.InputPin), StringComparer.Ordinal);
        var maxOutputPins = response.Connections
            .GroupBy(connection => connection.SourceNodeId.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(connection => connection.OutputPin), StringComparer.Ordinal);

        flow.Nodes = response.Nodes.Select(node =>
        {
            var id = node.Id.Trim();
            var customProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["portLayout"] = "top-bottom"
            };
            if (!string.IsNullOrWhiteSpace(node.Notes))
            {
                customProperties["notes"] = node.Notes.Trim();
            }
            if (maxInputPins.TryGetValue(id, out var inputPins))
            {
                customProperties["inputPins"] = inputPins.ToString(CultureInfo.InvariantCulture);
            }
            if (maxOutputPins.TryGetValue(id, out var outputPins))
            {
                customProperties["outputPins"] = outputPins.ToString(CultureInfo.InvariantCulture);
            }

            return new FlowNode
            {
                Id = id,
                Type = nodeTypes[id],
                Title = node.Title.Trim(),
                Description = node.Description?.Trim() ?? string.Empty,
                ChildFlowId = null,
                CustomProperties = customProperties
            };
        }).ToList();

        flow.Connections = response.Connections.Select(connection => new FlowConnection
        {
            Id = connection.Id.Trim(),
            SourceNodeId = connection.SourceNodeId.Trim(),
            TargetNodeId = connection.TargetNodeId.Trim(),
            SourcePort = $"output_{connection.OutputPin}",
            TargetPort = $"input_{connection.InputPin}",
            Label = string.IsNullOrWhiteSpace(connection.Label) ? null : connection.Label.Trim()
        }).ToList();

        ApplyFlowDesignerLayout(flow);

        var validation = validator.Validate(flow);
        var errors = validation.Issues.Where(issue => issue.Severity == ValidationSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new FlowDiagramGenerationException(
                "The AI proposal is not a valid Flow Designer diagram: "
                + string.Join(" ", errors.Select(error => error.Message)));
        }

        var evidence = ValidateEvidence(response.Evidence, sourceDocumentIds, nodeIds, connectionIds);
        var warnings = (response.Warnings ?? [])
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Select(warning => warning.Trim())
            .Concat(validation.Issues
                .Where(issue => issue.Severity == ValidationSeverity.Warning)
                .Select(issue => issue.Message))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var referencedElements = evidence.Select(item => item.ElementId).ToHashSet(StringComparer.Ordinal);
        foreach (var node in flow.Nodes.Where(node => node.Type is not NodeType.Start
                                                      and not NodeType.End
                                                      and not NodeType.Section
                                                      and not NodeType.Annotation))
        {
            if (!referencedElements.Contains(node.Id))
            {
                warnings.Add($"Generated node '{node.Title}' has no source evidence reference.");
            }
        }

        return new ParsedFlowDiagram(flow, evidence, warnings.Distinct(StringComparer.Ordinal).ToList());
    }

    private static IReadOnlyList<FlowDesignerEvidenceReference> ValidateEvidence(
        IReadOnlyList<GeneratedEvidence>? generatedEvidence,
        IReadOnlyCollection<string> sourceDocumentIds,
        IReadOnlySet<string> nodeIds,
        IReadOnlySet<string> connectionIds)
    {
        var sourceIds = sourceDocumentIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var evidence = new List<FlowDesignerEvidenceReference>();
        foreach (var item in generatedEvidence ?? [])
        {
            ValidateIdentifier(item.ElementId, "Evidence element ID");
            ValidateIdentifier(item.SourceDocumentId, "Evidence source document ID");
            ValidateText(item.Location, "Evidence location", 300);
            ValidateText(item.Explanation, "Evidence explanation", 500, required: true);

            var elementId = item.ElementId.Trim();
            if (!nodeIds.Contains(elementId) && !connectionIds.Contains(elementId))
            {
                throw new FlowDiagramGenerationException(
                    $"Evidence references unknown Flow Designer element '{elementId}'.");
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
                item.Explanation.Trim()));
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
        foreach (var node in flow.Nodes.Where(node => !visited.Contains(node.Id)))
        {
            levels[node.Id] = cycleLevel;
        }

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

    private sealed class GeneratedDiagramResponse
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string DiagramType { get; set; } = string.Empty;
        public List<GeneratedNode> Nodes { get; set; } = [];
        public List<GeneratedConnection> Connections { get; set; } = [];
        public List<GeneratedEvidence> Evidence { get; set; } = [];
        public List<string> Warnings { get; set; } = [];
    }

    private sealed class GeneratedNode
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
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
        public string ElementId { get; set; } = string.Empty;
        public string SourceDocumentId { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string Explanation { get; set; } = string.Empty;
    }
}