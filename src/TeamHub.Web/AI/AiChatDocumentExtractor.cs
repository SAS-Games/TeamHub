using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TeamHub.AI.Chat.Contracts;
using TeamHub.AI.Contracts;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace TeamHub.Web.AI;

public sealed class AiChatUploadDocument
{
    public string Name { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public string? Content { get; set; }
    public string? Base64Content { get; set; }
}

public interface IAiChatDocumentExtractor
{
    Task<AiChatPreparedAttachments> ExtractAsync(
        IReadOnlyList<AiChatUploadDocument> uploads,
        int maximumDocuments,
        int maximumCharacters,
        CancellationToken cancellationToken = default);
}

public sealed record AiChatPreparedAttachments(
    IReadOnlyList<AiChatDocument> Documents,
    IReadOnlyList<AiImageInput> Images);

public sealed partial class AiChatDocumentExtractor : IAiChatDocumentExtractor
{
    public const int MaximumBinaryFileBytes = 10 * 1024 * 1024;
    public const int MaximumTotalBinaryBytes = 20 * 1024 * 1024;
    private const long MaximumChmExpandedBytes = 50L * 1024 * 1024;
    private const int MaximumChmFiles = 2_000;
    private const long MaximumDocxExpandedBytes = 50L * 1024 * 1024;
    private const int MaximumDocxEntries = 2_000;

    public async Task<AiChatPreparedAttachments> ExtractAsync(
        IReadOnlyList<AiChatUploadDocument> uploads,
        int maximumDocuments,
        int maximumCharacters,
        CancellationToken cancellationToken = default)
    {
        if (uploads.Count > maximumDocuments)
            throw new AiChatException($"Attach no more than {maximumDocuments} documents.");

        var documents = new List<AiChatDocument>(uploads.Count);
        var images = new List<AiImageInput>();
        var totalCharacters = 0;
        var totalBinaryBytes = 0;
        foreach (var upload in uploads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(upload.Name?.Trim() ?? string.Empty);
            if (name.Length is 0 or > 200)
                throw new AiChatException("Every attached document needs a valid file name.");

            var extension = Path.GetExtension(name).ToLowerInvariant();
            if (extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif")
            {
                var bytes = DecodeBinary(upload, name);
                totalBinaryBytes = checked(totalBinaryBytes + bytes.Length);
                if (totalBinaryBytes > MaximumTotalBinaryBytes)
                    throw new AiChatException("Binary document and image uploads cannot exceed 20 MB in total.");
                var imageMediaType = ValidateImage(bytes, extension, name);
                images.Add(new AiImageInput(name, imageMediaType, Convert.ToBase64String(bytes)));
                continue;
            }

            string extracted;
            string mediaType;
            if (extension is ".pdf" or ".chm" or ".docx")
            {
                var bytes = DecodeBinary(upload, name);
                totalBinaryBytes = checked(totalBinaryBytes + bytes.Length);
                if (totalBinaryBytes > MaximumTotalBinaryBytes)
                    throw new AiChatException("Binary document and image uploads cannot exceed 20 MB in total.");

                var remaining = maximumCharacters - totalCharacters;
                extracted = extension switch
                {
                    ".pdf" => ExtractPdf(bytes, name, remaining, cancellationToken),
                    ".docx" => ExtractDocx(bytes, name, remaining, cancellationToken),
                    _ => await ExtractChmAsync(bytes, name, remaining, cancellationToken)
                };
                mediaType = "text/markdown";
            }
            else
            {
                mediaType = NormalizeTextMediaType(upload.MediaType, extension, name);
                extracted = upload.Content?.Trim() ?? string.Empty;
                if (extracted.Length == 0) throw new AiChatException($"Document '{name}' is empty.");
            }

            totalCharacters = checked(totalCharacters + extracted.Length);
            if (totalCharacters > maximumCharacters)
                throw new AiChatException(
                    $"Extracted document text exceeds the configured {maximumCharacters:N0}-character limit. "
                    + "Attach a smaller document or ask an administrator to increase the TeamHub Assistant document limit.");
            documents.Add(new AiChatDocument(name, mediaType, extracted));
        }
        return new AiChatPreparedAttachments(documents, images);
    }

    private static string ValidateImage(byte[] bytes, string extension, string name)
    {
        var valid = extension switch
        {
            ".png" => bytes.Length >= 8
                && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            ".jpg" or ".jpeg" => bytes.Length >= 3
                && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
            ".gif" => bytes.Length >= 6
                && (bytes.AsSpan(0, 6).SequenceEqual("GIF87a"u8)
                    || bytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8)),
            ".webp" => bytes.Length >= 12
                && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
        if (!valid) throw new AiChatException($"Image '{name}' does not match its file type.");
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "image/gif"
        };
    }

    private static byte[] DecodeBinary(AiChatUploadDocument upload, string name)
    {
        var value = upload.Base64Content?.Trim() ?? string.Empty;
        if (value.Length == 0) throw new AiChatException($"Document '{name}' did not contain uploaded file data.");
        var maximumBase64Length = ((MaximumBinaryFileBytes + 2L) / 3L * 4L) + 4L;
        if (value.Length > maximumBase64Length)
            throw new AiChatException($"Document '{name}' exceeds the 10 MB binary file limit.");
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length > MaximumBinaryFileBytes)
                throw new AiChatException($"Document '{name}' exceeds the 10 MB binary file limit.");
            return bytes;
        }
        catch (FormatException exception)
        {
            throw new AiChatException($"Document '{name}' contains invalid uploaded file data.", exception);
        }
    }

    private static string NormalizeTextMediaType(string? supplied, string extension, string name)
    {
        var mediaType = (supplied ?? string.Empty).Split(';', 2)[0].Trim().ToLowerInvariant();
        if (extension is ".md" or ".markdown") return "text/markdown";
        if (extension == ".csv") return "text/csv";
        if (extension == ".json") return "application/json";
        if (extension == ".txt") return "text/plain";
        if (mediaType is "text/plain" or "text/markdown" or "text/csv" or "application/json")
            return mediaType;
        throw new AiChatException(
            $"Document '{name}' is not a supported text, Markdown, CSV, JSON, Word (.docx), PDF, or CHM file.");
    }

    private static string ExtractDocx(
        byte[] bytes,
        string name,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        if (bytes.Length < 4
            || bytes[0] != 0x50
            || bytes[1] != 0x4b
            || (bytes[2] != 0x03 && bytes[2] != 0x05 && bytes[2] != 0x07))
            throw new AiChatException($"Document '{name}' is not a valid Word (.docx) file.");

        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > MaximumDocxEntries)
                throw new AiChatException($"Document '{name}' contains too many embedded files.");

            long expandedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                expandedBytes = checked(expandedBytes + entry.Length);
                if (expandedBytes > MaximumDocxExpandedBytes)
                    throw new AiChatException($"Document '{name}' expands beyond the 50 MB safety limit.");
            }

            var documentEntry = archive.Entries.SingleOrDefault(entry =>
                entry.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase));
            if (documentEntry is null)
                throw new AiChatException($"Document '{name}' does not contain a readable Word document body.");

            using var documentStream = documentEntry.Open();
            using var xmlReader = XmlReader.Create(documentStream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumDocxExpandedBytes
            });
            var document = XDocument.Load(xmlReader, LoadOptions.None);
            XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var result = new StringBuilder();
            foreach (var paragraph in document.Descendants(word + "p"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = new StringBuilder();
                foreach (var element in paragraph.Descendants())
                {
                    if (element.Name == word + "t") line.Append(element.Value);
                    else if (element.Name == word + "tab") line.Append('\t');
                    else if (element.Name == word + "br" || element.Name == word + "cr") line.AppendLine();
                }

                var text = line.ToString().Trim();
                if (text.Length == 0) continue;
                if (result.Length > 0) result.AppendLine();
                result.Append(text);
                EnsureWithinLimit(result.Length, maximumCharacters, name);
            }

            if (result.Length == 0)
                throw new AiChatException($"Document '{name}' contains no extractable text.");
            return result.ToString();
        }
        catch (AiChatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new AiChatException(
                $"Document '{name}' could not be read as Word (.docx). It may be damaged or password protected.",
                exception);
        }
    }

    private static string ExtractPdf(
        byte[] bytes,
        string name,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            throw new AiChatException($"Document '{name}' is not a valid PDF file.");

        try
        {
            using var document = PdfDocument.Open(bytes);
            var result = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = ContentOrderTextExtractor.GetText(page).Trim();
                if (text.Length == 0) continue;
                AppendSection(result, $"Page {page.Number}", text);
                EnsureWithinLimit(result.Length, maximumCharacters, name);
            }
            if (result.Length == 0)
                throw new AiChatException(
                    $"Document '{name}' has no extractable text. Scanned PDFs require OCR before attachment.");
            return result.ToString();
        }
        catch (AiChatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new AiChatException(
                $"Document '{name}' could not be read as a PDF. It may be damaged or password protected.", exception);
        }
    }

    private static async Task<string> ExtractChmAsync(
        byte[] bytes,
        string name,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        if (bytes.Length < 4 || !bytes.AsSpan(0, 4).SequenceEqual("ITSF"u8))
            throw new AiChatException($"Document '{name}' is not a valid CHM file.");
        if (!OperatingSystem.IsWindows())
            throw new AiChatException("CHM attachments require the Windows TeamHub host.");

        var helpExecutable = Path.Combine(Environment.SystemDirectory, "hh.exe");
        if (!File.Exists(helpExecutable))
            throw new AiChatException("Windows HTML Help is unavailable on this TeamHub server.");

        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"teamhub-ai-chm-{Guid.NewGuid():N}");
        var inputPath = Path.Combine(temporaryRoot, "document.chm");
        var outputPath = Path.Combine(temporaryRoot, "content");
        Directory.CreateDirectory(outputPath);
        try
        {
            await File.WriteAllBytesAsync(inputPath, bytes, cancellationToken);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = helpExecutable,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = temporaryRoot
                }
            };
            process.StartInfo.ArgumentList.Add("-decompile");
            process.StartInfo.ArgumentList.Add(outputPath);
            process.StartInfo.ArgumentList.Add(inputPath);
            if (!process.Start()) throw new AiChatException($"Document '{name}' could not be opened as CHM.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new AiChatException($"Document '{name}' took too long to extract.");
            }

            var files = Directory.EnumerateFiles(outputPath, "*", SearchOption.AllDirectories)
                .Where(IsReadableChmPage)
                .Take(MaximumChmFiles + 1)
                .ToList();
            if (files.Count > MaximumChmFiles)
                throw new AiChatException($"Document '{name}' contains too many help pages.");
            if (files.Count == 0)
                throw new AiChatException($"Document '{name}' contains no readable HTML or text help pages.");

            var expandedBytes = files.Sum(file => new FileInfo(file).Length);
            if (expandedBytes > MaximumChmExpandedBytes)
                throw new AiChatException($"Document '{name}' expands beyond the 50 MB safety limit.");

            var result = new StringBuilder();
            foreach (var file in files.OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                var raw = await File.ReadAllTextAsync(file, cancellationToken);
                var text = Path.GetExtension(file).Equals(".txt", StringComparison.OrdinalIgnoreCase)
                    ? raw.Trim()
                    : HtmlToText(raw);
                if (text.Length == 0) continue;
                var relativeName = Path.GetRelativePath(outputPath, file).Replace('\\', '/');
                AppendSection(result, relativeName, text);
                EnsureWithinLimit(result.Length, maximumCharacters, name);
            }
            if (result.Length == 0)
                throw new AiChatException($"Document '{name}' contains no extractable text.");
            return result.ToString();
        }
        finally
        {
            TryDeleteDirectory(temporaryRoot);
        }
    }

    private static bool IsReadableChmPage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".htm" or ".html" or ".txt" or ".hhc" or ".hhk";

    internal static string HtmlToText(string html)
    {
        var withoutCode = ScriptAndStyleRegex().Replace(html, " ");
        var withBreaks = BreakRegex().Replace(withoutCode, "\n");
        var withoutTags = HtmlTagRegex().Replace(withBreaks, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags).Replace('\u00a0', ' ');
        var compact = HorizontalWhitespaceRegex().Replace(decoded, " ");
        return ExcessBlankLinesRegex().Replace(compact, "\n\n").Trim();
    }

    private static void AppendSection(StringBuilder builder, string heading, string content)
    {
        if (builder.Length > 0) builder.AppendLine().AppendLine();
        builder.Append("## ").AppendLine(heading);
        builder.Append(content);
    }

    private static void EnsureWithinLimit(int actual, int maximum, string name)
    {
        if (actual <= maximum) return;
        throw new AiChatException(
            $"Extracted text from '{name}' exceeds the remaining {Math.Max(0, maximum):N0}-character limit. "
            + "Attach a smaller document or ask an administrator to increase the TeamHub Assistant document limit.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var tempPath = Path.GetFullPath(Path.GetTempPath());
            if (fullPath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullPath).StartsWith("teamhub-ai-chm-", StringComparison.Ordinal))
                Directory.Delete(fullPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex ScriptAndStyleRegex();

    [GeneratedRegex(@"<(br\s*/?|/p|/div|/li|/tr|/h[1-6])\s*>", RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline, matchTimeoutMilliseconds: 1_000)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"[^\S\r\n]+", RegexOptions.None, matchTimeoutMilliseconds: 1_000)]
    private static partial Regex HorizontalWhitespaceRegex();

    [GeneratedRegex(@"(?:\r?\n\s*){3,}", RegexOptions.None, matchTimeoutMilliseconds: 1_000)]
    private static partial Regex ExcessBlankLinesRegex();
}
