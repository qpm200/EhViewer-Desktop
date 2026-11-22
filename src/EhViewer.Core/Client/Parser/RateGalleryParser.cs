using System.Text.Json;
using EhViewer.Core.Client.Exceptions;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses gallery rating API responses (<c>rating_avg</c> and <c>rating_cnt</c>).
/// </summary>
public static class RateGalleryParser
{
    public sealed record Result(float Rating, int RatingCount);

    public static Result Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            JsonElement root = ParserUtils.ParseJsonObject(body);
            float rating = ParserUtils.GetRequiredSingle(root, "rating_avg");
            int ratingCount = ParserUtils.GetRequiredInt32(root, "rating_cnt");
            return new Result(rating, ratingCount);
        }
        catch (JsonException ex)
        {
            throw new ParseException("Can't parse rate gallery", body, ex);
        }
    }
}
