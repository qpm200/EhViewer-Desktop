using EhViewer.Core.Client;
using EhViewer.Core.Network;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Adapts <see cref="DesktopSettings"/> to the <see cref="IEhClientSettings"/> contract consumed by
/// <c>EhViewer.Core</c>. Every getter reads through to the live document, so a settings change is
/// observed by networking on the next request without rebuilding the client factory.
/// </summary>
public sealed class DesktopClientSettings : IEhClientSettings
{
    private readonly Func<DesktopSettings> _accessor;
    private readonly AppPaths _paths;

    public DesktopClientSettings(Func<DesktopSettings> accessor, AppPaths paths)
    {
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    private DesktopSettings Current => _accessor();

    public bool BuiltInHosts => Current.BuiltInHosts;
    public bool BuiltInExHosts => Current.BuiltInExHosts;
    public bool UseDnsOverHttps => Current.UseDnsOverHttps;
    public bool DomainFronting => Current.DomainFronting;
    public string DohEndpoint => Current.DohEndpoint;
    public EhProxyType ProxyType => Current.ProxyType;
    public string? ProxyIp => Current.ProxyIp;
    public int ProxyPort => Current.ProxyPort;
    public EhSite GallerySite => Current.GallerySite;
    public int ThumbResolution => Current.ThumbResolution;
    public bool FixThumbUrl => Current.FixThumbUrl;
    public bool ShowJpnTitle => Current.ShowJpnTitle;
    public bool ShowGalleryPages => Current.ShowGalleryPages;
    public bool SaveParseErrorBody => Current.SaveParseErrorBody;
    public string ImageResolution => Current.ImageResolution;
    public string? ParseErrorDirectory => _paths.ParseErrorDirectory;
}
