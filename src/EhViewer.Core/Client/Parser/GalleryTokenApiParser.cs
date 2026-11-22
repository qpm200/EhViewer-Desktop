using System.Text.Json;
using EhViewer.Core.Client.Exceptions;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses JSON <c>tokenlist</c> responses returned by the <c>gtoken</c> API method.
/// </summary>
public static class GalleryTokenApiParser
{
    public static string Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        JsonElement root = ParserUtils.ParseJsonObject(body);
        JsonElement tokenList = ParserUtils.GetRequiredProperty(root, "tokenlist");
        if (tokenList.ValueKind != JsonValueKind.Array || tokenList.GetArrayLength() == 0)
        {
            throw new JsonException("Missing or empty 'tokenlist' array.");
        }

        JsonElement jo = tokenList[0];
        string? token = ParserUtils.GetOptionalString(jo, "token");
        if (token is not null)
        {
            return token;
        }

        throw new EhException(ParserUtils.GetRequiredString(jo, "error"));
    }
}
