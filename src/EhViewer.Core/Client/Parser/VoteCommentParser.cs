using System.Text.Json;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses comment vote API responses.
/// </summary>
public static class VoteCommentParser
{
    public sealed record Result(long Id, int Score, int Vote, int ExpectVote);

    public static Result Parse(string body, int vote)
    {
        ArgumentNullException.ThrowIfNull(body);

        JsonElement root = ParserUtils.ParseJsonObject(body);
        long id = ParserUtils.GetRequiredInt64(root, "comment_id");
        int score = ParserUtils.GetRequiredInt32(root, "comment_score");
        int returnedVote = ParserUtils.GetRequiredInt32(root, "comment_vote");
        return new Result(id, score, returnedVote, vote);
    }
}
