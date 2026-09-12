using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Services;

internal static class RayTracingKnowledgeMapTemplate
{
    public const string TemplateKey = "RAY_TRACING_MASTER_OVERVIEW";
    public const int Version = 3;

    public static readonly Guid RootFlowId = Guid.Parse("3fab98af-6765-49e4-b669-a3b02699b6aa");
    private static readonly Guid AccelerationFlowId = Guid.Parse("629f2f41-8881-431c-bfc8-ef9c719247e9");
    private static readonly Guid RayGenerationFlowId = Guid.Parse("ec0e6aca-bbd1-47d3-846a-125c57bb57ab");
    private static readonly Guid TraversalFlowId = Guid.Parse("22ae4f70-4cae-4134-9fed-52d7e73d9cfd");
    private static readonly Guid HitMissFlowId = Guid.Parse("7334fcb0-bc96-41af-ae4a-01c5b0fcc9d3");
    private static readonly Guid LightingFlowId = Guid.Parse("22e4c744-486d-49c0-bd84-e437f0b78dac");

    public static FlowDiagramTemplateBundle Create() => new()
    {
        RootFlowId = RootFlowId,
        Flows =
        [
            CreateOverview(),
            CreateAccelerationStructures(),
            CreateRayGeneration(),
            CreateTraversal(),
            CreateHitMissProcessing(),
            CreateLighting()
        ]
    };

    private static FlowDefinition CreateOverview()
    {
        var flow = Flow(
            RootFlowId,
            "Ray Tracing - Master Overview",
            "A beginner-friendly map of a complete ray-tracing renderer, with drill-down diagrams for the core tracing stages.",
            "overview");
        flow.Nodes =
        [
            Node("overview-start", NodeType.Start, "Scene / Frame Data", "Geometry, instances, materials, lights, camera data, transforms, and optional history buffers.", 60, 260, tone: "green"),
            Node("overview-accel", NodeType.Subprocess, "Prepare Acceleration Structures", "Build or update the scene structures used to accelerate intersection searches.", 330, 260, AccelerationFlowId, "Vulkan represents acceleration structures as opaque, implementation-dependent objects.", "blue"),
            Node("overview-raygen", NodeType.Subprocess, "Ray Generation", "Create primary rays for pixels or samples and begin tracing.", 600, 260, RayGenerationFlowId, "Vulkan ray tracing pipelines launch ray-generation shader invocations with vkCmdTraceRaysKHR; traceRayEXT begins a traversal.", "blue"),
            Node("overview-traversal", NodeType.Subprocess, "Ray Traversal + Primitive Intersection", "Search the top-level and bottom-level structures and test candidate primitives.", 870, 260, TraversalFlowId, "Vulkan defines acceleration-structure traversal as fixed-function; the internal hierarchy remains implementation-defined.", "blue"),
            Node("overview-hit", NodeType.Subprocess, "Hit / Miss Processing", "Filter candidates where applicable, then handle the closest accepted hit or a miss.", 1140, 260, HitMissFlowId, "In a Vulkan ray tracing pipeline, any-hit can filter candidates; closest-hit or miss handles the final outcome.", "blue"),
            Node("overview-lighting", NodeType.Subprocess, "Lighting + Secondary Rays", "Shade the result and optionally launch shadow, reflection, transmission, or indirect rays.", 1410, 260, LightingFlowId, "Secondary rays repeat ray traversal, primitive intersection, and hit/miss processing.", "blue"),
            Node("overview-result", NodeType.DataStore, "Ray-Traced Result", "Store noisy radiance or another ray-traced effect plus any renderer-required guide data.", 1680, 260, notes: "This is renderer output, not part of acceleration-structure traversal.", tone: "purple"),
            Node("overview-denoise", NodeType.Process, "Denoising", "Optionally reduce low-sample noise using spatial, temporal, or combined filtering.", 1950, 260, notes: "Denoising is renderer-level processing and is not intrinsic to ray traversal."),
            Node("overview-composite", NodeType.Process, "Composite / Post Process", "Combine ray-traced results with other passes, then apply display-oriented processing.", 2220, 260, notes: "Composition, tone mapping, and presentation are renderer-level stages."),
            Node("overview-end", NodeType.End, "Final Frame", "Present or store the completed frame.", 2490, 260, tone: "red")
        ];
        flow.Connections =
        [
            Edge("overview-e01", "overview-start", "overview-accel"),
            Edge("overview-e02", "overview-accel", "overview-raygen"),
            Edge("overview-e03", "overview-raygen", "overview-traversal"),
            Edge("overview-e04", "overview-traversal", "overview-hit"),
            Edge("overview-e05", "overview-hit", "overview-lighting"),
            Edge("overview-e06", "overview-lighting", "overview-result", "Current path complete"),
            Edge("overview-e07", "overview-result", "overview-denoise"),
            Edge("overview-e08", "overview-denoise", "overview-composite"),
            Edge("overview-e09", "overview-composite", "overview-end"),
            Edge("overview-e10", "overview-lighting", "overview-traversal", "Secondary rays")
        ];
        return flow;
    }

    private static FlowDefinition CreateAccelerationStructures()
    {
        var flow = Flow(
            AccelerationFlowId,
            "Ray Tracing - Acceleration Structures",
            "How geometry and instances are prepared in bottom-level and top-level acceleration structures.",
            "acceleration");
        flow.Nodes =
        [
            Node("accel-start", NodeType.Start, "Geometry + Instance Inputs", "Collect triangle or procedural geometry, transforms, masks, and application identifiers.", 60, 300, tone: "green"),
            Node("accel-change", NodeType.Decision, "What Changed?", "Choose work from actual scene changes and the update capabilities selected during the original build.", 330, 290, tone: "orange"),
            Node("accel-update-check", NodeType.Decision, "Compatible Update Allowed?", "An update requires the original build to allow updates and replacement inputs to meet API constraints.", 620, 300, tone: "orange"),
            Node("accel-update", NodeType.Preparation, "Update Bottom-Level Structures", "Reuse compatible structure storage for changing geometry when appropriate.", 910, 220, notes: "Vulkan uses build flags and update mode to control permitted updates; rebuilding remains a valid policy choice.", tone: "green"),
            Node("accel-rebuild", NodeType.Preparation, "Rebuild Bottom-Level Structures", "Perform a fresh build when updates are unavailable, incompatible, or no longer desirable.", 910, 430, tone: "orange"),
            Node("accel-reuse", NodeType.Process, "Reuse Bottom-Level Structures", "Keep unchanged geometry structures and update only the instance-level data that needs it.", 620, 570),
            Node("accel-bottom", NodeType.DataStore, "Bottom-Level Structure Set", "Represents geometry used by one or more scene instances.", 1200, 300, notes: "Vulkan commonly calls these BLAS. A BLAS can contain triangle geometry or procedural axis-aligned bounding-box geometry.", tone: "purple"),
            Node("accel-instances", NodeType.Process, "Create / Update Instances", "Reference bottom-level structures and provide transforms, masks, and application data.", 1470, 300),
            Node("accel-top", NodeType.DataStore, "Build / Update Top-Level Structure", "Organize the current scene instances for world-space traversal.", 1740, 300, notes: "Vulkan commonly calls this a TLAS. Whether it is built, updated, or reused depends on scene changes and application policy.", tone: "purple"),
            Node("accel-sync", NodeType.Process, "Make Builds Available", "Ensure completed acceleration-structure writes are visible before rays read them.", 2010, 300, notes: "Vulkan requires appropriate synchronization between acceleration-structure build/update work and later tracing."),
            Node("accel-end", NodeType.End, "Structures Ready", "Ray traversal can now use the prepared scene structures.", 2280, 300, tone: "red")
        ];
        flow.Connections =
        [
            Edge("accel-e01", "accel-start", "accel-change"),
            Edge("accel-e03", "accel-change", "accel-update-check", "Geometry new / changed"),
            Edge("accel-e04", "accel-change", "accel-reuse", "Instances only", "output_2"),
            Edge("accel-e05", "accel-update-check", "accel-update", "Yes"),
            Edge("accel-e06", "accel-update-check", "accel-rebuild", "No", "output_2"),
            Edge("accel-e08", "accel-update", "accel-bottom"),
            Edge("accel-e09", "accel-rebuild", "accel-bottom"),
            Edge("accel-e10", "accel-reuse", "accel-bottom"),
            Edge("accel-e11", "accel-bottom", "accel-instances"),
            Edge("accel-e12", "accel-instances", "accel-top"),
            Edge("accel-e13", "accel-top", "accel-sync"),
            Edge("accel-e14", "accel-sync", "accel-end")
        ];
        return flow;
    }

    private static FlowDefinition CreateRayGeneration()
    {
        var flow = Flow(
            RayGenerationFlowId,
            "Ray Tracing - Ray Generation",
            "How a rendering sample becomes a primary ray and starts an intersection search.",
            "ray-generation");
        flow.Nodes =
        [
            Node("raygen-start", NodeType.Start, "Launch Rendering Work", "Begin a grid or batch of ray-generation work for the current view.", 60, 260, notes: "A Vulkan ray tracing pipeline uses vkCmdTraceRaysKHR or vkCmdTraceRaysIndirectKHR.", tone: "green"),
            Node("raygen-sample", NodeType.Process, "Choose Pixel / Sample", "Map the current invocation to a pixel, sample, probe, or other renderer-defined task.", 330, 260),
            Node("raygen-camera", NodeType.DataStore, "Read Camera + Frame Data", "Load camera transforms, image dimensions, jitter, and other sampling inputs.", 600, 260, tone: "purple"),
            Node("raygen-ray", NodeType.Preparation, "Construct Primary Ray", "Compute a world-space origin and direction for the selected sample.", 870, 260, tone: "blue"),
            Node("raygen-range", NodeType.Process, "Set Ray Controls", "Set the valid distance interval and optional visibility mask, flags, and result state.", 1140, 260, notes: "Vulkan traceRayEXT accepts origin, direction, minimum and maximum distance, a cull mask, ray flags, and payload location."),
            Node("raygen-trace", NodeType.Subprocess, "Begin Traversal + Intersection", "Submit the primary ray to the scene acceleration structure.", 1410, 260, notes: "Continue into the traversal detail shown from the master diagram.", tone: "blue"),
            Node("raygen-end", NodeType.End, "Await Hit / Miss Result", "Continue when tracing returns the renderer-defined result data.", 1680, 260, tone: "red")
        ];
        flow.Connections =
        [
            Edge("raygen-e01", "raygen-start", "raygen-sample"),
            Edge("raygen-e02", "raygen-sample", "raygen-camera"),
            Edge("raygen-e03", "raygen-camera", "raygen-ray"),
            Edge("raygen-e04", "raygen-ray", "raygen-range"),
            Edge("raygen-e05", "raygen-range", "raygen-trace"),
            Edge("raygen-e06", "raygen-trace", "raygen-end")
        ];
        return flow;
    }

    private static FlowDefinition CreateTraversal()
    {
        var flow = Flow(
            TraversalFlowId,
            "Ray Tracing - Traversal + Intersection",
            "How a ray searches scene instances and geometry for the closest accepted intersection.",
            "traversal");
        flow.Nodes =
        [
            Node("trav-start", NodeType.Start, "Ray + Search Controls", "Receive origin, direction, distance interval, mask, flags, and result state.", 60, 320, tone: "green"),
            Node("trav-top", NodeType.DataStore, "Traverse Top-Level Structure", "Search world-space bounding regions that organize scene instances.", 330, 320, notes: "Vulkan calls this a top-level acceleration structure (TLAS). Its internal hierarchy is implementation-defined.", tone: "purple"),
            Node("trav-instance-bounds", NodeType.Decision, "Instance Region Overlaps Ray?", "Reject non-overlapping regions and continue the top-level search.", 600, 310, tone: "orange"),
            Node("trav-instance", NodeType.Process, "Select Candidate Instance", "Read its bottom-level reference, transform, mask, and application data.", 870, 100),
            Node("trav-mask", NodeType.Decision, "Visibility Mask Passes?", "Continue only when the ray and instance masks overlap.", 1140, 90, tone: "orange"),
            Node("trav-transform", NodeType.Preparation, "Transform Ray as Needed", "Express the ray in the instance's object space while preserving the valid search interval.", 1410, 90, notes: "The API exposes world-to-object and object-to-world transforms to relevant ray tracing shader stages."),
            Node("trav-bottom", NodeType.DataStore, "Traverse Referenced Bottom-Level Structure", "Search bounding regions that organize the selected instance's geometry.", 1680, 90, notes: "Vulkan calls this a bottom-level acceleration structure (BLAS). Do not assume a particular BVH layout.", tone: "purple"),
            Node("trav-geometry-bounds", NodeType.Decision, "Geometry Region Overlaps Ray?", "Prune regions that cannot contain a closer valid intersection.", 1950, 80, tone: "orange"),
            Node("trav-primitive", NodeType.Decision, "Primitive Type?", "Choose the intersection rule for triangle or procedural geometry.", 2220, 80, tone: "orange"),
            Node("trav-triangle", NodeType.Process, "Built-In Triangle Test", "Test the ray and produce distance plus barycentric attributes for a valid candidate.", 2490, 0),
            Node("trav-procedural", NodeType.Process, "Custom Primitive Test", "Run an application-defined intersection test for procedural bounding-box geometry.", 2490, 300, notes: "In Vulkan, an intersection shader can report candidate intersections with reportIntersectionEXT."),
            Node("trav-candidate", NodeType.Process, "Candidate Intersection", "Carry intersection distance and attributes into any applicable candidate filtering.", 2760, 150),
            Node("trav-accepted", NodeType.Decision, "Candidate Accepted?", "Rejected candidates are ignored; accepted nearer candidates become the best hit so far.", 3030, 140, notes: "A Vulkan any-hit shader is invoked only when applicable and may ignore a candidate or terminate the search.", tone: "orange"),
            Node("trav-closest", NodeType.DataStore, "Update Closest Accepted Intersection", "Keep the nearest accepted candidate and use its distance to prune farther work.", 3300, 60, tone: "purple"),
            Node("trav-more", NodeType.Decision, "More Relevant Search Work?", "Continue until useful regions are exhausted or an allowed early-termination rule ends the search.", 3570, 300, tone: "orange"),
            Node("trav-next", NodeType.Process, "Select Next Bounding Region", "Continue through top-level or bottom-level regions without assuming a fixed traversal order.", 3300, 520),
            Node("trav-result", NodeType.Decision, "Closest Accepted Intersection Exists?", "Choose a surface-hit outcome when one remains; otherwise choose a miss outcome.", 3840, 290, tone: "orange"),
            Node("trav-hit", NodeType.Process, "Surface Hit", "Provide the closest accepted intersection and attributes to hit processing.", 4110, 100, tone: "green"),
            Node("trav-miss", NodeType.Process, "Miss", "Report that no accepted intersection was found in the ray interval.", 4110, 500, tone: "orange"),
            Node("trav-end", NodeType.End, "Traversal Outcome Ready", "Continue to hit or miss processing.", 4380, 300, tone: "red")
        ];
        flow.Connections =
        [
            Edge("trav-e01", "trav-start", "trav-top"),
            Edge("trav-e02", "trav-top", "trav-instance-bounds"),
            Edge("trav-e03", "trav-instance-bounds", "trav-instance", "Yes"),
            Edge("trav-e04", "trav-instance-bounds", "trav-more", "No", "output_2"),
            Edge("trav-e05", "trav-instance", "trav-mask"),
            Edge("trav-e06", "trav-mask", "trav-transform", "Yes"),
            Edge("trav-e07", "trav-mask", "trav-more", "No", "output_2"),
            Edge("trav-e08", "trav-transform", "trav-bottom"),
            Edge("trav-e09", "trav-bottom", "trav-geometry-bounds"),
            Edge("trav-e10", "trav-geometry-bounds", "trav-primitive", "Yes"),
            Edge("trav-e11", "trav-geometry-bounds", "trav-more", "No", "output_2"),
            Edge("trav-e12", "trav-primitive", "trav-triangle", "Triangle"),
            Edge("trav-e13", "trav-primitive", "trav-procedural", "Procedural", "output_2"),
            Edge("trav-e14", "trav-triangle", "trav-candidate"),
            Edge("trav-e15", "trav-procedural", "trav-candidate"),
            Edge("trav-e16", "trav-candidate", "trav-accepted"),
            Edge("trav-e17", "trav-accepted", "trav-closest", "Yes"),
            Edge("trav-e18", "trav-accepted", "trav-more", "No", "output_2"),
            Edge("trav-e19", "trav-closest", "trav-more"),
            Edge("trav-e20", "trav-more", "trav-next", "Yes"),
            Edge("trav-e21", "trav-more", "trav-result", "No", "output_2"),
            Edge("trav-e22", "trav-next", "trav-instance-bounds", "Continue"),
            Edge("trav-e23", "trav-result", "trav-hit", "Yes"),
            Edge("trav-e24", "trav-result", "trav-miss", "No", "output_2"),
            Edge("trav-e25", "trav-hit", "trav-end"),
            Edge("trav-e26", "trav-miss", "trav-end")
        ];
        return flow;
    }

    private static FlowDefinition CreateHitMissProcessing()
    {
        var flow = Flow(
            HitMissFlowId,
            "Ray Tracing - Hit / Miss Processing",
            "How candidate filtering and the final closest-hit or miss outcome feed renderer shading.",
            "hit-miss");
        flow.Nodes =
        [
            Node("hit-start", NodeType.Start, "Traversal Reports Progress", "Receive a candidate intersection during traversal or the final result after traversal ends.", 60, 300, tone: "green"),
            Node("hit-kind", NodeType.Decision, "Candidate or Final Outcome?", "Candidate processing can repeat; final processing chooses hit or miss once.", 330, 290, tone: "orange"),
            Node("hit-filter", NodeType.Decision, "Candidate Filtering Applies?", "Opaque geometry commonly skips this step; filtered geometry may test opacity or other rules.", 620, 80, notes: "In a Vulkan ray tracing pipeline this optional stage is an any-hit shader.", tone: "orange"),
            Node("hit-any", NodeType.Process, "Evaluate Candidate", "Accept it, ignore it, or accept it and request early search termination.", 910, 20, notes: "Vulkan any-hit shaders may use ignoreIntersectionEXT or terminateRayEXT when those behaviors are required."),
            Node("hit-accept", NodeType.Decision, "Accepted?", "Only an accepted candidate can become the closest intersection found so far.", 1180, 70, tone: "orange"),
            Node("hit-update", NodeType.Process, "Keep Nearest Accepted Candidate", "Update the best hit and continue traversal unless early termination was requested.", 1450, 0, tone: "green"),
            Node("hit-ignore", NodeType.Process, "Ignore Candidate", "Leave the current best hit unchanged and continue searching.", 1450, 220),
            Node("hit-resume", NodeType.Connector, "Resume Traversal", "Return to the traversal and primitive-intersection search.", 1720, 110, notes: "Any-hit is candidate filtering, not a replacement for final closest-hit or miss processing.", tone: "blue"),
            Node("hit-final", NodeType.Decision, "Closest Accepted Hit?", "After traversal, branch on whether an accepted intersection remains.", 620, 500, tone: "orange"),
            Node("hit-surface", NodeType.Process, "Process Surface Hit", "Use intersection attributes, instance and geometry identifiers, and material data for shading.", 910, 420, notes: "In a Vulkan ray tracing pipeline this is commonly a closest-hit shader."),
            Node("hit-miss", NodeType.Process, "Process Miss", "Return sky, environment, visibility, or another renderer-defined miss value.", 910, 650, notes: "In a Vulkan ray tracing pipeline this is commonly a miss shader."),
            Node("hit-output", NodeType.DataStore, "Hit / Miss Result", "Return renderer-defined result data to the ray's caller.", 1180, 520, tone: "purple"),
            Node("hit-end", NodeType.End, "Continue Shading", "Use the result for lighting, visibility, or another renderer task.", 1450, 520, tone: "red")
        ];
        flow.Connections =
        [
            Edge("hit-e01", "hit-start", "hit-kind"),
            Edge("hit-e02", "hit-kind", "hit-filter", "Candidate"),
            Edge("hit-e03", "hit-kind", "hit-final", "Final", "output_2"),
            Edge("hit-e04", "hit-filter", "hit-any", "Yes"),
            Edge("hit-e05", "hit-filter", "hit-update", "No", "output_2"),
            Edge("hit-e06", "hit-any", "hit-accept"),
            Edge("hit-e07", "hit-accept", "hit-update", "Yes"),
            Edge("hit-e08", "hit-accept", "hit-ignore", "No", "output_2"),
            Edge("hit-e09", "hit-update", "hit-resume"),
            Edge("hit-e10", "hit-ignore", "hit-resume"),
            Edge("hit-e11", "hit-final", "hit-surface", "Yes"),
            Edge("hit-e12", "hit-final", "hit-miss", "No", "output_2"),
            Edge("hit-e13", "hit-surface", "hit-output"),
            Edge("hit-e14", "hit-miss", "hit-output"),
            Edge("hit-e15", "hit-output", "hit-end")
        ];
        return flow;
    }

    private static FlowDefinition CreateLighting()
    {
        var flow = Flow(
            LightingFlowId,
            "Ray Tracing - Lighting + Secondary Rays",
            "How hit or miss data becomes radiance and may generate additional rays.",
            "lighting");
        flow.Nodes =
        [
            Node("light-start", NodeType.Start, "Hit / Miss Result", "Begin with a surface hit, environment result, or visibility result returned by tracing.", 60, 300, tone: "green"),
            Node("light-kind", NodeType.Decision, "Surface Hit?", "A miss can contribute environment lighting without surface material evaluation.", 330, 290, tone: "orange"),
            Node("light-env", NodeType.Process, "Evaluate Environment", "Read sky, environment, or another renderer-defined miss contribution.", 620, 520),
            Node("light-material", NodeType.Process, "Evaluate Surface Material", "Use attributes, textures, normals, and the material's scattering model.", 620, 80),
            Node("light-emission", NodeType.Process, "Add Emission", "Include energy emitted by the visible surface when applicable.", 890, 80),
            Node("light-direct", NodeType.Process, "Estimate Direct Lighting", "Sample lights and evaluate the material response.", 1160, 80),
            Node("light-shadow", NodeType.Process, "Trace Visibility Ray", "Test whether sampled light reaches the surface; transparent handling is renderer-defined.", 1430, 80, notes: "A visibility ray is a secondary ray and repeats traversal plus hit/miss processing.", tone: "blue"),
            Node("light-accumulate", NodeType.Process, "Accumulate Current Contribution", "Combine emission, visible direct light, and any environment contribution.", 1700, 300, tone: "green"),
            Node("light-continue", NodeType.Decision, "Continue Path?", "Stop at a depth or throughput rule; a path tracer may also use probabilistic termination.", 1970, 290, tone: "orange"),
            Node("light-sample", NodeType.Process, "Sample Scattering Direction", "Choose reflection, transmission, or another supported material event.", 2240, 60),
            Node("light-secondary", NodeType.Connector, "Return to Traversal + Intersection", "Generate a secondary ray and repeat traversal, intersection, hit/miss processing, and shading.", 2510, 60, notes: "Vulkan permits pipeline trace-ray instructions from closest-hit and miss shaders, subject to the pipeline recursion-depth limit.", tone: "blue"),
            Node("light-result", NodeType.DataStore, "Return Ray-Traced Result", "Return accumulated radiance or another renderer-defined effect to the caller.", 2240, 520, tone: "purple"),
            Node("light-end", NodeType.End, "Lighting Result Ready", "The renderer may now denoise or composite the result.", 2510, 520, notes: "Denoising and composition occur outside ray traversal and are intentionally shown only on the master diagram.", tone: "red")
        ];
        flow.Connections =
        [
            Edge("light-e01", "light-start", "light-kind"),
            Edge("light-e02", "light-kind", "light-material", "Yes"),
            Edge("light-e03", "light-kind", "light-env", "No", "output_2"),
            Edge("light-e04", "light-material", "light-emission"),
            Edge("light-e05", "light-emission", "light-direct"),
            Edge("light-e06", "light-direct", "light-shadow"),
            Edge("light-e07", "light-shadow", "light-accumulate"),
            Edge("light-e08", "light-env", "light-accumulate"),
            Edge("light-e09", "light-accumulate", "light-continue"),
            Edge("light-e10", "light-continue", "light-sample", "Yes"),
            Edge("light-e11", "light-sample", "light-secondary"),
            Edge("light-e12", "light-secondary", "light-start", "After tracing returns"),
            Edge("light-e13", "light-continue", "light-result", "No", "output_2"),
            Edge("light-e14", "light-result", "light-end")
        ];
        return flow;
    }

    private static FlowDefinition Flow(Guid id, string name, string description, string suffix) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        DiagramType = DiagramType.StandardFlowchart,
        Metadata = new(StringComparer.OrdinalIgnoreCase)
        {
            ["seedKey"] = $"ray-tracing-knowledge-map-v3/{suffix}",
            ["officialVulkanGuide"] = "https://docs.vulkan.org/guide/latest/extensions/ray_tracing.html",
            ["officialVulkanSpec"] = "https://docs.vulkan.org/spec/latest/chapters/raytracing.html",
            ["reviewedOn"] = "2026-09-13"
        },
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
        Version = 0
    };

    private static FlowNode Node(
        string id,
        NodeType type,
        string title,
        string description,
        double x,
        double y,
        Guid? childFlowId = null,
        string notes = "",
        string tone = "") => new()
    {
        Id = id,
        Type = type,
        Title = title,
        Description = description,
        X = x,
        Y = y,
        Width = 220,
        ChildFlowId = childFlowId,
        CustomProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            ["notes"] = notes,
            ["portLayout"] = "horizontal",
            ["presentationStyle"] = childFlowId.HasValue ? "card" : string.Empty,
            ["tone"] = tone
        }
    };

    private static FlowConnection Edge(
        string id,
        string source,
        string target,
        string? label = null,
        string sourcePort = "output_1") => new()
    {
        Id = id,
        SourceNodeId = source,
        TargetNodeId = target,
        SourcePort = sourcePort,
        TargetPort = "input_1",
        Label = label
    };
}
