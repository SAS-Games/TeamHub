using Microsoft.Extensions.Configuration;

namespace TeamHub.Team;

internal sealed class ManagedExcelSourceFileStore(IConfiguration configuration) : IExcelSourceFileStore
{
    public async Task<StoredExcelSource> SaveAsync(
        Stream source,
        string fileName,
        long length,
        CancellationToken cancellationToken = default)
    {
        var displayName = Path.GetFileName(fileName)?.Trim() ?? string.Empty;
        if (displayName.Length == 0 || !displayName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select an .xlsx Excel workbook.", nameof(fileName));
        if (displayName.Length > 260)
            throw new ArgumentException("The Excel file name cannot exceed 260 characters.", nameof(fileName));
        if (length is <= 0 or > DirectDownloadExcelTableSourceReader.MaximumWorkbookBytes)
            throw new ArgumentException("The Excel workbook must be between 1 byte and 25 MB.", nameof(length));

        var root = ExcelSourceStorage.UploadRoot(configuration);
        Directory.CreateDirectory(root);
        var reference = $"{Guid.NewGuid():N}.xlsx";
        var path = Path.Combine(root, reference);
        await using var destination = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);
        var buffer = new byte[81920];
        var total = 0L;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > DirectDownloadExcelTableSourceReader.MaximumWorkbookBytes)
                throw new ArgumentException("The Excel workbook is larger than the 25 MB limit.", nameof(source));
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (total == 0) throw new ArgumentException("The selected Excel workbook is empty.", nameof(source));
        return new StoredExcelSource(reference, displayName);
    }
}

internal static class ExcelSourceStorage
{
    public static string UploadRoot(IConfiguration configuration)
    {
        var configured = configuration["TeamExcel:UploadDirectory"];
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "data", "team-excel")
            : configured.Trim();
        return Path.GetFullPath(path);
    }
}
