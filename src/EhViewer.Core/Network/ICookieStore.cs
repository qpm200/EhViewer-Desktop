namespace EhViewer.Core.Network;

/// <summary>
/// Cookie jar abstraction used by HTTP clients and account state checks.
/// </summary>
public interface ICookieStore
{
    bool HasSignedIn { get; }
    void AddCookie(EhCookie cookie);
    void SaveFromResponse(Uri requestUrl, IEnumerable<string> setCookieHeaders);
    IReadOnlyList<EhCookie> LoadForRequest(Uri requestUrl);
    IReadOnlyList<EhCookie> GetCookies(Uri url);
    string GetCookieHeader(Uri url);
    bool Contains(Uri url, string name);
    void SignOut();
    void Clear();
}
