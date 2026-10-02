using System.Net.Mail;

namespace TeamHub.Application.Models;

public static class WorkflowAssigneeEmails
{
    private static readonly char[] Separators = [',', ';', '\r', '\n'];

    public static IReadOnlyList<string> Parse(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Normalize)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    public static string Format(IEnumerable<string> emails) =>
        string.Join("; ", emails
            .Select(Normalize)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    public static bool IsValid(string email) =>
        MailAddress.TryCreate(email?.Trim(), out var parsed)
        && string.Equals(parsed.Address, email?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) =>
        MailAddress.TryCreate(value.Trim(), out var parsed)
            ? parsed.Address.ToLowerInvariant()
            : value.Trim().ToLowerInvariant();
}
