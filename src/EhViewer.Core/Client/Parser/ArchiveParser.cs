using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using EhViewer.Core.Client.Data;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses H@H download options and Archiver GP/Credits costs from <c>archiver.php</c>.
/// </summary>
public static partial class ArchiveParser
{
    public sealed record ArchiveItem(string Res, string Name);
    public sealed record ParseResult(string ParamOr, ArchiveItem[] Items);

    private static readonly Regex PatternForm = CreateFormPattern();
    private static readonly Regex PatternArchive = CreateArchivePattern();
    private static readonly Regex PatternDownloadUrl = CreateDownloadUrlPattern();

    public static ParseResult Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        Match m = PatternForm.Match(body);
        string paramOr = m.Success ? m.Groups[1].Value : string.Empty;

        var items = new List<ArchiveItem>();
        foreach (Match match in PatternArchive.Matches(body))
        {
            string res = ParserUtils.Trim(match.Groups[1].Value);
            string name = ParserUtils.Trim(match.Groups[2].Value);
            items.Add(new ArchiveItem(res, name));
        }

        return new ParseResult(paramOr, [.. items]);
    }

    public static ArchiverData ParseArchiver(string body, EhSite site = EhSite.E)
    {
        ArgumentNullException.ThrowIfNull(body);

        var data = new ArchiverData();
        IHtmlDocument document = EhHtmlHelper.ParseDocument(body);
        IElement? dbElement = document.GetElementById("db") ?? EhHtmlHelper.GetChildElement(document.Body, 0);
        if (dbElement is null)
        {
            return data;
        }

        // E-site archiver pages include extra top blocks before funds and original/resample options.
        int fundsIndex = site == EhSite.E ? 2 : 0;
        int optionsContainerIndex = site == EhSite.E ? 3 : 1;

        IElement? fundsElement = EhHtmlHelper.GetChildElement(dbElement, fundsIndex);
        if (fundsElement is null)
        {
            return data;
        }

        data.Funds = EhHtmlHelper.GetNormalizedText(fundsElement);

        IElement? container = EhHtmlHelper.GetChildElement(dbElement, optionsContainerIndex);
        if (container is null || container.Children.Length < 2)
        {
            return data;
        }

        if (TryExtractArchiveOption(container.Children[0], out string? origCost, out string? origSize, out string? origUrl) &&
            TryExtractArchiveOption(container.Children[1], out string? resCost, out string? resSize, out string? resUrl))
        {
            data.OriginalCost = origCost;
            data.OriginalSize = origSize;
            data.OriginalUrl = origUrl;
            data.ResampleCost = resCost;
            data.ResampleSize = resSize;
            data.ResampleUrl = resUrl;
        }

        return data;
    }

    public static string? ParseArchiverDownloadUrl(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        Match m = PatternDownloadUrl.Match(body);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool TryExtractArchiveOption(
        IElement optionContainer,
        out string? cost,
        out string? size,
        out string? actionUrl)
    {
        cost = null;
        size = null;
        actionUrl = null;

        IElement? costEl = EhHtmlHelper.GetChildElement(EhHtmlHelper.GetChildElement(optionContainer, 0), 0);
        IElement? formEl = EhHtmlHelper.GetChildElement(optionContainer, 1);
        IElement? sizeEl = EhHtmlHelper.GetChildElement(EhHtmlHelper.GetChildElement(optionContainer, 2), 0);
        if (costEl is null || formEl is null || sizeEl is null)
        {
            return false;
        }

        cost = EhHtmlHelper.GetNormalizedText(costEl);
        size = EhHtmlHelper.GetNormalizedText(sizeEl);
        actionUrl = formEl.GetAttribute("action") ?? string.Empty;
        return true;
    }

    [GeneratedRegex(@"<form id=""hathdl_form"" action=""[^""]*?or=([^=""]*?)"" method=""post"">", RegexOptions.CultureInvariant)]
    private static partial Regex CreateFormPattern();

    [GeneratedRegex(@"<a href=""[^""]*"" onclick=""return do_hathdl\('([0-9]+|org)'\)"">([^<]+)</a>", RegexOptions.CultureInvariant)]
    private static partial Regex CreateArchivePattern();

    [GeneratedRegex(@"href=""(.*)"">Click Here To Start Downloading", RegexOptions.CultureInvariant)]
    private static partial Regex CreateDownloadUrlPattern();
}
