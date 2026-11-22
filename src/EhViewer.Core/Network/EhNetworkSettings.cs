using EhViewer.Core.Client;

namespace EhViewer.Core.Network;

public enum EhProxyType
{
    Direct = 0,
    System = 1,
    Http = 2,
    Socks = 3
}

/// <summary>
/// Platform-neutral settings contract consumed by networking, parsing, database, and tag subsystems in <c>EhViewer.Core</c>.
/// </summary>
public interface IEhClientSettings
{
    bool BuiltInHosts { get; }
    bool BuiltInExHosts { get; }
    bool UseDnsOverHttps { get; }
    bool DomainFronting { get; }
    string DohEndpoint { get; }
    EhProxyType ProxyType { get; }
    string? ProxyIp { get; }
    int ProxyPort { get; }
    EhSite GallerySite { get; }
    int ThumbResolution { get; }
    bool FixThumbUrl { get; }
    bool ShowJpnTitle { get; }
    bool ShowGalleryPages { get; }
    bool SaveParseErrorBody { get; }
    string ImageResolution { get; }
    string? ParseErrorDirectory { get; }
}

/// <summary>
/// Default mutable implementation of <see cref="IEhClientSettings"/> for tests and standalone Core usage.
/// </summary>
public sealed class EhClientSettings : IEhClientSettings
{
    public bool BuiltInHosts { get; set; } = true;
    public bool BuiltInExHosts { get; set; } = true;
    public bool UseDnsOverHttps { get; set; } = false;
    public bool DomainFronting { get; set; } = true;
    public string DohEndpoint { get; set; } = "https://77.88.8.1/dns-query";
    public EhProxyType ProxyType { get; set; } = EhProxyType.System;
    public string? ProxyIp { get; set; }
    public int ProxyPort { get; set; } = -1;
    public EhSite GallerySite { get; set; } = EhSite.E;
    public int ThumbResolution { get; set; } = 0;
    public bool FixThumbUrl { get; set; } = false;
    public bool ShowJpnTitle { get; set; } = false;
    public bool ShowGalleryPages { get; set; } = false;
    public bool SaveParseErrorBody { get; set; } = true;
    public string ImageResolution { get; set; } = EhConfig.ImageSizeAuto;
    public string? ParseErrorDirectory { get; set; }
}
