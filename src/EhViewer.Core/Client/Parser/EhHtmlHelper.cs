using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using EhViewer.Core.Client.Exceptions;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// AngleSharp DOM helpers mirroring upstream <c>JsoupUtils</c> semantics.
/// </summary>
public static partial class EhHtmlHelper
{
    private const string OffensiveWarning =
        "<p>(And if you choose to ignore this warning, you lose all rights to complain about it in the future.)</p>";
    private const string PiningWarning =
        "<p>This gallery is pining for the fjords.</p>";

    private static readonly ThreadLocal<HtmlParser> SharedParser = new(static () => new HtmlParser());
    private static readonly Regex PatternStandardError = CreateStandardErrorPattern();
    private static readonly HashSet<string> BlockTagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "br", "dd", "div", "dl", "dt",
        "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4",
        "h5", "h6", "header", "hr", "li", "main", "nav", "noscript", "ol", "p", "pre",
        "section", "table", "tbody", "td", "tfoot", "th", "thead", "tr", "ul"
    };
    public static IHtmlDocument ParseDocument(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return SharedParser.Value!.ParseDocument(html);
    }

    public static IElement ParseBodyFragment(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        HtmlParser parser = SharedParser.Value!;
        IHtmlDocument doc = parser.ParseDocument("<!DOCTYPE html><html><body></body></html>");
        IElement body = doc.Body!;
        INodeList nodes = parser.ParseFragment(html, body);
        foreach (INode node in nodes.ToArray())
        {
            body.AppendChild(node);
        }
        return body;
    }

    public static IElement? GetElementByClass(IDocument doc, string className)
    {
        ArgumentNullException.ThrowIfNull(doc);
        IHtmlCollection<IElement> elements = doc.GetElementsByClassName(className);
        return elements.Length > 0 ? elements[0] : null;
    }

    public static IElement? GetElementByClass(IElement element, string className)
    {
        ArgumentNullException.ThrowIfNull(element);
        IHtmlCollection<IElement> elements = element.GetElementsByClassName(className);
        return elements.Length > 0 ? elements[0] : null;
    }

    public static IHtmlCollection<IElement>? GetElementsByClass(IElement element, string className)
    {
        ArgumentNullException.ThrowIfNull(element);
        IHtmlCollection<IElement> elements = element.GetElementsByClassName(className);
        return elements.Length > 0 ? elements : null;
    }

    public static IElement? GetElementByTag(IElement element, string tagName)
    {
        ArgumentNullException.ThrowIfNull(element);
        IHtmlCollection<IElement> elements = element.GetElementsByTagName(tagName);
        return elements.Length > 0 ? elements[0] : null;
    }

    public static IElement? GetChildElement(IElement? element, int index)
    {
        if (element is null || index < 0)
        {
            return null;
        }

        IHtmlCollection<IElement> children = element.Children;
        return index < children.Length ? children[index] : null;
    }

    /// <summary>
    /// Extracts normalized combined text matching Jsoup <c>Element.text()</c>:
    /// collapses ASCII/NBSP whitespace runs to a single space, separates block elements, and trims.
    /// </summary>
    public static string GetNormalizedText(IElement? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        bool lastWasWhite = true;
        AppendNormalizedText(element, sb, ref lastWasWhite);

        int end = sb.Length;
        while (end > 0 && sb[end - 1] == ' ')
        {
            end--;
        }
        return sb.ToString(0, end);
    }

    /// <summary>
    /// Extracts normalized direct-text content matching Jsoup <c>Element.ownText()</c>:
    /// considers only direct <see cref="IText"/> child nodes and ignores child elements.
    /// </summary>
    public static string GetOwnNormalizedText(IElement? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        bool lastWasWhite = true;
        foreach (INode child in element.ChildNodes)
        {
            if (child is IText)
            {
                AppendNormalizedText(child, sb, ref lastWasWhite);
            }
        }

        int end = sb.Length;
        while (end > 0 && sb[end - 1] == ' ')
        {
            end--;
        }
        return sb.ToString(0, end);
    }

    private static void AppendNormalizedText(INode node, StringBuilder sb, ref bool lastWasWhite)
    {
        if (node is IText textNode)
        {
            string data = textNode.Data;
            for (int i = 0; i < data.Length; i++)
            {
                char c = data[i];
                if (IsJsoupWhitespace(c))
                {
                    if (!lastWasWhite && sb.Length > 0)
                    {
                        sb.Append(' ');
                        lastWasWhite = true;
                    }
                }
                else
                {
                    sb.Append(c);
                    lastWasWhite = false;
                }
            }
            return;
        }

        if (node is IElement el)
        {
            bool isBlock = BlockTagNames.Contains(el.TagName);
            if (isBlock && !lastWasWhite && sb.Length > 0)
            {
                sb.Append(' ');
                lastWasWhite = true;
            }

            foreach (INode child in el.ChildNodes)
            {
                AppendNormalizedText(child, sb, ref lastWasWhite);
            }

            if (isBlock && !lastWasWhite && sb.Length > 0)
            {
                sb.Append(' ');
                lastWasWhite = true;
            }
        }
    }

    public static void ThrowIfSiteError(string body, bool checkWarnings = false)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (checkWarnings)
        {
            if (body.Contains(OffensiveWarning, StringComparison.Ordinal))
            {
                throw new OffensiveException();
            }

            if (body.Contains(PiningWarning, StringComparison.Ordinal))
            {
                throw new PiningException();
            }
        }

        Match errorMatch = PatternStandardError.Match(body);
        if (errorMatch.Success)
        {
            throw new EhException(errorMatch.Groups[1].Value);
        }
    }

    public sealed record HtmlLinkSpan(string Label, string Url);

    /// <summary>Converts an HTML fragment (such as a gallery comment or EventPane) to readable plain text.</summary>
    public static string HtmlToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        IElement fragment = ParseBodyFragment(html.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase));
        return GetNormalizedText(fragment);
    }

    /// <summary>Extracts HTTP/HTTPS hyperlink spans from an HTML fragment (e.g. gallery comments).</summary>
    public static IReadOnlyList<HtmlLinkSpan> ExtractLinks(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        IElement fragment = ParseBodyFragment(html);
        var links = new List<HtmlLinkSpan>();
        foreach (IElement anchor in fragment.QuerySelectorAll("a[href]"))
        {
            string? href = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(href, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            string label = GetNormalizedText(anchor);
            links.Add(new HtmlLinkSpan(string.IsNullOrWhiteSpace(label) ? uri.ToString() : label, uri.ToString()));
        }

        return links;
    }

    private static bool IsJsoupWhitespace(char c) =>
        c is ' ' or '\t' or '\n' or '\f' or '\r' or '\u00A0';

    [GeneratedRegex(@"<div class=""d"">\r?\n<p>([^<]+)</p>", RegexOptions.CultureInvariant)]
    private static partial Regex CreateStandardErrorPattern();
}
