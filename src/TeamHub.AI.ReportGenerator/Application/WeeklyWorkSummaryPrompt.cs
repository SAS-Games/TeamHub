using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TeamHub.AI.ReportGenerator.Contracts;

namespace TeamHub.AI.ReportGenerator.Application;

internal sealed record WeeklyWorkSummaryPrompt(string Instructions, string Version)
{
    public static async Task<WeeklyWorkSummaryPrompt> LoadAsync(
        IOptions<ReportGeneratorOptions> options,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(options.Value.WeeklyWorkSummaryPromptPath, AppContext.BaseDirectory);
        string instructions;
        try
        {
            instructions = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new WeeklyWorkSummaryException(
                "The Weekly Work Summary instruction file could not be read. Ask an administrator to check AI:ReportGenerator:WeeklyWorkSummaryPromptPath.");
        }
        if (string.IsNullOrWhiteSpace(instructions))
            throw new WeeklyWorkSummaryException("The Weekly Work Summary instruction file is empty. No report was generated.");
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instructions)));
        return new WeeklyWorkSummaryPrompt(instructions, version);
    }
}
