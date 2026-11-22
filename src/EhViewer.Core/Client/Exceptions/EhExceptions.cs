namespace EhViewer.Core.Client.Exceptions;

public class EhException : Exception
{
    public EhException(string message) : base(message)
    {
    }

    public EhException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

public class ParseException : EhException
{
    public string? Body { get; }

    public ParseException(string message, string? body) : base(message)
    {
        Body = body;
    }

    public ParseException(string message, string? body, Exception? innerException)
        : base(message, innerException)
    {
        Body = body;
    }
}

public sealed class EmptyGalleryException : EhException
{
    public string? Body { get; }

    public EmptyGalleryException(string message, string? body) : base(message)
    {
        Body = body;
    }

    public EmptyGalleryException(string message, string? body, Exception? innerException)
        : base(message, innerException)
    {
        Body = body;
    }
}

public sealed class OffensiveException : EhException
{
    public OffensiveException() : base("OFFENSIVE")
    {
    }
}

public sealed class PiningException : EhException
{
    public PiningException() : base("pining for the fjords")
    {
    }
}

public sealed class GalleryUnavailableException : EhException
{
    public GalleryUnavailableException()
        : base("此画廊已被下架\nThis gallery is unavailable")
    {
    }
}

public sealed class Image509Exception : EhException
{
    public Image509Exception() : base("509")
    {
    }
}

public sealed class NoHAtHClientException : EhException
{
    public NoHAtHClientException(string message) : base(message)
    {
    }
}

public sealed class CancelledException : Exception
{
    public CancelledException() : base("CANCELED")
    {
    }

    public CancelledException(Exception? innerException) : base("CANCELED", innerException)
    {
    }
}

public sealed class StatusCodeException : Exception
{
    private const string DefaultErrorMessage = "Error response code";

    private static readonly IReadOnlyDictionary<int, string> DefaultMessages = new Dictionary<int, string>
    {
        [400] = "Bad Request",
        [401] = "Unauthorized",
        [402] = "Payment Required",
        [403] = "Forbidden",
        [404] = "Not Found",
        [405] = "Method Not Allowed",
        [406] = "Not Acceptable",
        [407] = "Proxy Authentication Required",
        [408] = "Request Timeout",
        [409] = "Conflict",
        [410] = "Gone",
        [411] = "Length Required",
        [412] = "Precondition Failed",
        [413] = "Payload Too Large",
        [414] = "URI Too Long",
        [415] = "Unsupported Media Type",
        [416] = "Range Not Satisfiable",
        [417] = "Expectation Failed",
        [500] = "Internal Server Error",
        [501] = "Not Implemented",
        [502] = "Bad Gateway",
        [503] = "Service Unavailable",
        [504] = "Gateway Timeout",
        [505] = "HTTP Version Not Supported",
    };

    public int ResponseCode { get; }
    public bool IsIdentifiedResponseCode => !string.Equals(Message, DefaultErrorMessage, StringComparison.Ordinal);

    public StatusCodeException(int responseCode)
        : base(DefaultMessages.GetValueOrDefault(responseCode, DefaultErrorMessage))
    {
        ResponseCode = responseCode;
    }

    public StatusCodeException(int responseCode, string message) : base(message)
    {
        ResponseCode = responseCode;
    }
}
