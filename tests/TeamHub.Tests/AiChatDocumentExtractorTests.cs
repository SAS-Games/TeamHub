using FluentAssertions;
using TeamHub.AI.Chat.Contracts;
using TeamHub.Web.AI;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace TeamHub.Tests;

public sealed class AiChatDocumentExtractorTests
{
    private readonly AiChatDocumentExtractor extractor = new();

    [Fact]
    public async Task Pdf_attachment_is_extracted_as_prompt_text()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText("Quarterly support summary", 12, new PdfPoint(30, 700), font);
        var bytes = builder.Build();

        var result = await extractor.ExtractAsync(
            [Binary("report.pdf", "application/pdf", bytes)],
            5,
            20_000);

        result.Images.Should().BeEmpty();
        result.Documents.Should().ContainSingle();
        result.Documents[0].MediaType.Should().Be("text/markdown");
        result.Documents[0].Content.Should().Contain("Quarterly support summary");
    }

    [Fact]
    public async Task Png_attachment_is_preserved_as_a_multimodal_image()
    {
        byte[] pngHeader = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

        var result = await extractor.ExtractAsync(
            [Binary("screenshot.png", "image/png", pngHeader)],
            5,
            20_000);

        result.Documents.Should().BeEmpty();
        result.Images.Should().ContainSingle();
        result.Images[0].Name.Should().Be("screenshot.png");
        result.Images[0].MediaType.Should().Be("image/png");
        result.Images[0].Base64Data.Should().Be(Convert.ToBase64String(pngHeader));
    }

    [Fact]
    public async Task Invalid_chm_signature_is_rejected_before_decompilation()
    {
        var action = () => extractor.ExtractAsync(
            [Binary("manual.chm", "application/vnd.ms-htmlhelp", "not a chm"u8.ToArray())],
            5,
            20_000);

        await action.Should().ThrowAsync<AiChatException>()
            .WithMessage("*not a valid CHM*");
    }

    [Fact]
    public void Chm_html_conversion_removes_active_content_and_decodes_text()
    {
        var text = AiChatDocumentExtractor.HtmlToText(
            "<html><style>.x{display:none}</style><script>alert(1)</script><body><h1>Help &amp; Support</h1><p>Read this.</p></body></html>");

        text.Should().Contain("Help & Support")
            .And.Contain("Read this.")
            .And.NotContain("alert")
            .And.NotContain("display:none");
    }

    private static AiChatUploadDocument Binary(string name, string mediaType, byte[] bytes) => new()
    {
        Name = name,
        MediaType = mediaType,
        Base64Content = Convert.ToBase64String(bytes)
    };
}
