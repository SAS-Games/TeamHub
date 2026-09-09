using TeamHub.FlowDesigner.DependencyInjection;
using TeamHub.FlowDesigner.Web;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole();
builder.Services.AddRazorPages();

var databasePath = Path.Combine(builder.Environment.ContentRootPath, "data", "flowdesigner.db");
var templateDatabasePath = Path.Combine(builder.Environment.ContentRootPath, "data", "templates.db");
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
builder.Services.AddFlowDesigner(options =>
{
    options.ConnectionString = $"Data Source={databasePath}";
    options.TemplateConnectionString = $"Data Source={templateDatabasePath}";
});

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.MapFlowDesignerApi();

await app.Services.InitializeFlowDesignerAsync();
await app.RunAsync();

public partial class Program;
