using System.Text.RegularExpressions;
using EhViewer.Core.Client.Exceptions;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses forums login response HTML for the signed-in display name or forum error message.
/// </summary>
public static partial class SignInParser
{
    private static readonly Regex NamePattern = CreateNamePattern();
    private static readonly Regex ErrorPattern = CreateErrorPattern();

    public static string Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        Match nameMatch = NamePattern.Match(body);
        if (nameMatch.Success)
        {
            return nameMatch.Groups[1].Value;
        }

        Match errorMatch = ErrorPattern.Match(body);
        if (errorMatch.Success)
        {
            string message = errorMatch.Groups[1].Success
                ? errorMatch.Groups[1].Value
                : errorMatch.Groups[2].Value;
            throw new EhException(message);
        }

        throw new ParseException("Can't parse sign in", body);
    }

    [GeneratedRegex(@"<p>You are now logged in as: (.+?)<", RegexOptions.CultureInvariant)]
    private static partial Regex CreateNamePattern();

    [GeneratedRegex(
        @"(?:<h4>The error returned was:</h4>\s*<p>(.+?)</p>)|(?:<span class=""postcolor"">(.+?)</span>)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateErrorPattern();
}
