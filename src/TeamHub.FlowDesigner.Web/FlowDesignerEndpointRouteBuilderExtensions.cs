using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Web;

public static class FlowDesignerEndpointRouteBuilderExtensions
{
    public static RouteGroupBuilder MapFlowDesignerApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/flows");

        api.MapGet("/", async (IFlowService flows, CancellationToken cancellationToken) =>
            Results.Ok(await flows.ListAsync(cancellationToken)));

        api.MapGet("/published/{sourceFlowId:guid}", async (
            Guid sourceFlowId,
            IFlowPublicationWorkflowService publications,
            CancellationToken cancellationToken) =>
        {
            var published = await publications.GetPublishedBySourceAsync(sourceFlowId, cancellationToken);
            return published is null ? Results.NotFound() : Results.Ok(published.Definition);
        });

        api.MapGet("/published/{sourceFlowId:guid}/link-targets", async (
            Guid sourceFlowId,
            IFlowPublicationWorkflowService publications,
            CancellationToken cancellationToken) =>
        {
            var targets = (await publications.ListPublishedBundleAsync(sourceFlowId, cancellationToken))
                .Select(item => new { item.Id, item.Name, item.DiagramType });
            return Results.Ok(targets);
        });

        api.MapPut("/published/{sourceFlowId:guid}", async (
            Guid sourceFlowId,
            FlowDefinition flow,
            IFlowPublicationWorkflowService publications,
            CancellationToken cancellationToken) =>
        {
            if (sourceFlowId != flow.Id)
            {
                return Results.BadRequest(new { message = "The route and document IDs do not match." });
            }
            try
            {
                var updated = await publications.UpdatePublishedAsync(sourceFlowId, flow, cancellationToken);
                return Results.Ok(new { flow = updated, issues = Array.Empty<object>() });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        api.MapGet("/publication-requests/{requestId:guid}/snapshot", async (
            Guid requestId,
            Guid? flowId,
            IFlowPublicationWorkflowService publications,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (flowId.HasValue)
                {
                    var diagram = await publications.GetRequestDiagramAsync(requestId, flowId.Value, cancellationToken);
                    return diagram is null ? Results.NotFound() : Results.Ok(diagram);
                }
                var request = await publications.GetRequestAsync(requestId, cancellationToken);
                return request is null ? Results.NotFound() : Results.Ok(request.Snapshot);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        });

        api.MapPost("/{id:guid}/publication-requests", async (
            Guid id,
            IFlowPublicationWorkflowService publications,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await publications.RequestAsync(id, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        api.MapPost("/", async (
            CreateFlowRequest request,
            IFlowService flows,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var flow = await flows.CreateAsync(
                    request.Name,
                    request.Description,
                    request.DiagramType,
                    cancellationToken: cancellationToken);
                return Results.Created($"/api/flows/{flow.Id}", flow);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        });

        api.MapGet("/{id:guid}", async (Guid id, IFlowService flows, CancellationToken cancellationToken) =>
        {
            var flow = await flows.GetAsync(id, cancellationToken);
            return flow is null ? Results.NotFound() : Results.Ok(flow);
        });

        api.MapGet("/{id:guid}/link-targets", async (Guid id, IFlowService flows, CancellationToken cancellationToken) =>
        {
            var flow = await flows.GetAsync(id, cancellationToken);
            if (flow is null) return Results.NotFound();

            var targets = (await flows.ListLinkTargetsAsync(id, cancellationToken))
                .Select(item => new { item.Id, item.Name, item.DiagramType })
                .ToList();
            return Results.Ok(targets);
        });

        api.MapPost("/{id:guid}/children", async (
            Guid id,
            CreateChildFlowRequest request,
            IFlowService flows,
            IFlowPermissionService permissions,
            CancellationToken cancellationToken) =>
        {
            var parent = await flows.GetAsync(id, cancellationToken);
            if (parent is null) return Results.NotFound();
            if (!permissions.CanEdit(parent.CreatedBy)) return Results.Forbid();

            try
            {
                var child = await flows.CreateAsync(
                    request.Name,
                    diagramType: parent.DiagramType,
                    cancellationToken: cancellationToken);
                var validation = await flows.SaveAsync(child, cancellationToken);
                return validation.IsValid
                    ? Results.Created($"/api/flows/{child.Id}", child)
                    : Results.BadRequest(new { message = "The child diagram could not be created.", issues = validation.Issues });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        });

        api.MapPut("/{id:guid}", async (Guid id, FlowDefinition flow, IFlowService flows, CancellationToken cancellationToken) =>
        {
            if (id != flow.Id)
            {
                return Results.BadRequest(new { message = "The route and document IDs do not match." });
            }

            try
            {
                var result = await flows.SaveAsync(flow, cancellationToken);
                return result.IsValid
                    ? Results.Ok(new { flow, issues = result.Issues })
                    : Results.BadRequest(new { message = "The graph contains structural errors.", issues = result.Issues });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
        });

        api.MapPost("/{id:guid}/validate", (Guid id, FlowDefinition flow, IFlowValidator validator) =>
            id == flow.Id ? Results.Ok(validator.Validate(flow)) : Results.BadRequest());

        api.MapPost("/{id:guid}/templates", async (
            Guid id,
            SaveFlowTemplateRequest request,
            IFlowTemplateCatalogService templates,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await templates.SaveFlowAsync(id, request, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        api.MapDelete("/{id:guid}", async (Guid id, IFlowService flows, CancellationToken cancellationToken) =>
        {
            try
            {
                await flows.DeleteAsync(id, cancellationToken);
                return Results.NoContent();
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        api.MapPost("/{id:guid}/nodes/{nodeId}/comments", async (
            Guid id,
            string nodeId,
            AddNodeCommentRequest request,
            IFlowService flows,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await flows.AddNodeCommentAsync(id, nodeId, request.Body, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        return api;
    }

    public sealed record AddNodeCommentRequest(string Body);
    public sealed record CreateChildFlowRequest(string Name);
    public sealed record CreateFlowRequest(string Name, string? Description, DiagramType DiagramType);
}
