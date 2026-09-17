using Microsoft.AspNetCore.Razor.TagHelpers;
using TeamHub.Web.Formatting;

namespace TeamHub.Web.TagHelpers;

[HtmlTargetElement("safe-markdown")]
public sealed class SafeMarkdownTagHelper(ISafeMarkdownRenderer renderer) : TagHelper
{
    [HtmlAttributeName("text")]
    public string? Text { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.RemoveAll("text");
        output.Content.SetHtmlContent(renderer.ToHtml(Text));
    }
}
