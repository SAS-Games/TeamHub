using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Tests;

public sealed class RepositoryTests
{
    [Fact]
    public async Task SaveLoadListDelete_WorksWithSqlite()
    {
        await using var database = new TestDatabase();
        var repository = await database.CreateRepositoryAsync();
        var flow = ValidationTests.ConnectedFlow();

        await repository.SaveAsync(flow);
        var loaded = await repository.GetAsync(flow.Id);
        var list = await repository.ListAsync();

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Nodes.Count);
        Assert.Single(list);
        Assert.Equal(2, list[0].NodeCount);

        await repository.DeleteAsync(flow.Id);
        Assert.Null(await repository.GetAsync(flow.Id));
    }
}
