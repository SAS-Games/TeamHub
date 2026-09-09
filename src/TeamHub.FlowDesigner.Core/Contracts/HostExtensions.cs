using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Core.Contracts;

public interface ICurrentUserProvider
{
    string? GetCurrentUserId();
}

public interface IFlowPermissionService
{
    bool CanView(string? ownerId);
    bool CanEdit(string? ownerId);
    bool CanCreate();
    bool CanUseTemplate(FlowTemplate template);
}

public interface IFlowThemeProvider
{
    string? AccentColor { get; }
    string? CssClass { get; }
}

public interface IFlowNavigationProvider
{
    string FlowsPath { get; }
}
