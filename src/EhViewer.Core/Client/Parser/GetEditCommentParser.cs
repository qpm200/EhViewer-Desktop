using System.Text.Json;
using AngleSharp.Dom;
using EhViewer.Core.Client.Exceptions;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses the JSON/HTML fragment returned when opening a comment editor dialog.
/// </summary>
public static class GetEditCommentParser
{
    public sealed record Result(long Id, string Comment);

    public static Result Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            JsonElement root = ParserUtils.ParseJsonObject(body);
            if (root.TryGetProperty("error", out _))
            {
                throw new EhException(ParserUtils.GetRequiredString(root, "error"));
            }

            long id = ParserUtils.GetRequiredInt64(root, "comment_id");
            string editableCommentHtml = ParserUtils.GetRequiredString(root, "editable_comment");

            IElement bodyFragment = EhHtmlHelper.ParseBodyFragment(editableCommentHtml);
            IElement? textarea = bodyFragment.QuerySelector("textarea[name=commenttext_edit]");
            if (textarea is null)
            {
                throw new ParseException("Can't find editable comment", body);
            }

            return new Result(id, textarea.TextContent);
        }
        catch (JsonException ex)
        {
            throw new ParseException("Can't parse editable comment", body, ex);
        }
    }
}
