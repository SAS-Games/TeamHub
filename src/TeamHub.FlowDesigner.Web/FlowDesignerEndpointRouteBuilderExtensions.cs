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

        return api;
    }
}
