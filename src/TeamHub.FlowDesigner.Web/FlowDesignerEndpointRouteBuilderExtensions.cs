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

        api.MapGet("/{id:guid}", async (Guid id, IFlowService flows, CancellationToken cancellationToken) =>
        {
            var flow = await flows.GetAsync(id, cancellationToken);
            return flow is null ? Results.NotFound() : Results.Ok(flow);
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
        });

        api.MapPost("/{id:guid}/validate", (Guid id, FlowDefinition flow, IFlowValidator validator) =>
            id == flow.Id ? Results.Ok(validator.Validate(flow)) : Results.BadRequest());

        api.MapDelete("/{id:guid}", async (Guid id, IFlowService flows, CancellationToken cancellationToken) =>
        {
            try
            {
                await flows.DeleteAsync(id, cancellationToken);
                return Results.NoContent();
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
}
