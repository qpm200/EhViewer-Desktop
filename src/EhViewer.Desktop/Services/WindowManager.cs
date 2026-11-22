using EhViewer.Core.Client.Data;
using EhViewer.Desktop.Downloads;
using EhViewer.Desktop.Reader;
using EhViewer.Desktop.ViewModels;
using EhViewer.Desktop.Views;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Manages detached reader windows, deduplicated by gallery identity so opening an already
/// detached gallery brings the existing window to the front instead of creating duplicates.
/// </summary>
public sealed class WindowManager : IDisposable
{
    private readonly Func<ReaderViewModel> _createViewModel;
    private readonly DesktopSettingsHolder _settingsHolder;
    private readonly ILogger<WindowManager> _logger;
    private readonly Dictionary<string, ManagedReaderWindow> _windows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Window> _auxWindows = new(StringComparer.Ordinal);
    private readonly List<Window> _bossHiddenWindows = [];
    private bool _disposed;

    public WindowManager(
        Func<ReaderViewModel> createViewModel,
        DesktopSettingsHolder settingsHolder,
        ILogger<WindowManager> logger)
    {
        _createViewModel = createViewModel ?? throw new ArgumentNullException(nameof(createViewModel));
        _settingsHolder = settingsHolder ?? throw new ArgumentNullException(nameof(settingsHolder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public int OpenWindowCount => _windows.Count;
    public int BossHiddenWindowCount => _bossHiddenWindows.Count;

    /// <summary>Stable key used to deduplicate detached windows per gallery or local folder.</summary>
    internal static string BuildGalleryKey(long gid) => $"gid:{gid}";

    internal static string BuildLocalKey(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        return $"local:{Path.GetFullPath(directoryPath)}";
    }

    /// <summary>Opens a remote gallery in its own window, or activates an existing one.</summary>
    public async Task OpenGalleryAsync(GalleryDetail detail, int? startPage = null)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string key = BuildGalleryKey(detail.Gid);
        if (_windows.TryGetValue(key, out ManagedReaderWindow? existing))
        {
            existing.Window.Activate();
            if (startPage.HasValue)
            {
                await existing.ViewModel.ShowPageAsync(startPage.Value).ConfigureAwait(true);
            }

            return;
        }

        string title = EhViewer.Core.Client.EhUtils.GetSuitableTitle(detail) ?? $"画廊 {detail.Gid}";
        (Window window, ReaderViewModel viewModel) = CreateDetachedWindow(key, title);
        window.Activate();

        await viewModel.OpenDetailAsync(detail, startPage).ConfigureAwait(true);
    }

    /// <summary>Opens a local library gallery in its own window, or activates an existing one.</summary>
    public async Task OpenLocalAsync(LocalGallery gallery)
    {
        ArgumentNullException.ThrowIfNull(gallery);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string key = BuildLocalKey(gallery.DirectoryPath);
        if (_windows.TryGetValue(key, out ManagedReaderWindow? existing))
        {
            existing.Window.Activate();
            return;
        }

        (Window window, ReaderViewModel viewModel) = CreateDetachedWindow(key, gallery.Title);
        window.Activate();

        await viewModel
            .OpenLocalAsync(gallery.Title, gallery.DirectoryPath, gallery.StartPage, gallery.ImageCount)
            .ConfigureAwait(true);
    }

    private (Window Window, ReaderViewModel ViewModel) CreateDetachedWindow(string key, string title)
    {
        ReaderViewModel viewModel = _createViewModel();
        var page = new ReaderPage(viewModel);
        var window = new Window
        {
            Title = $"{title} — EhViewer Desktop",
            Content = page
        };

        DesktopSettings settings = _settingsHolder.Current;
        bool dark = WindowAppearance.IsEffectivelyDark(settings.Theme);
        if (settings.UseMica)
        {
            WindowAppearance.TryApplyBackdrop(window, 0x000000, dark);
        }

        MaterialThemeService.ApplyPalette(settings.UiTheme, settings.Theme, window);

        var entry = new ManagedReaderWindow(key, window, viewModel);
        _windows[key] = entry;

        page.CloseRequested += (_, _) => window.Close();
        page.FullscreenChanged += (_, fullscreen) =>
        {
            try
            {
                window.AppWindow.SetPresenter(fullscreen
                    ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
                    : Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
            }
            catch
            {
                // Ignore unsupported presenter transitions on virtualized / headless desktops.
            }
        };
        window.Closed += (_, _) => CloseTrackedEntry(key);

        return (window, viewModel);
    }

    private void CloseTrackedEntry(string key)
    {
        if (!_windows.Remove(key, out ManagedReaderWindow? entry))
        {
            return;
        }

        try
        {
            entry.ViewModel.SaveProgress();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "关闭独立阅读窗口时保存进度失败：{Key}", key);
        }

        entry.ViewModel.Dispose();
    }

    /// <summary>Opens or brings to front a tracked auxiliary window (e.g. detached gallery detail).</summary>
    public Window OpenOrActivateAuxWindow(string key, string title, Func<Window, UIElement> contentFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(contentFactory);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_auxWindows.TryGetValue(key, out Window? existing))
        {
            existing.Activate();
            return existing;
        }

        var window = new Window { Title = $"{title} — EhViewer Desktop" };
        window.Content = contentFactory(window);

        DesktopSettings settings = _settingsHolder.Current;
        bool dark = WindowAppearance.IsEffectivelyDark(settings.Theme);
        if (settings.UseMica)
        {
            WindowAppearance.TryApplyBackdrop(window, 0x000000, dark);
        }

        MaterialThemeService.ApplyPalette(settings.UiTheme, settings.Theme, window);
        WindowPlacementHelper.Restore(window.AppWindow, settings);
        _auxWindows[key] = window;
        window.Closed += (_, _) => _auxWindows.Remove(key);
        window.Activate();
        return window;
    }

    /// <summary>Immediately hides all open detached reader and gallery windows for Boss Key privacy.</summary>
    public void HideAllForBossKey()
    {
        foreach (ManagedReaderWindow entry in _windows.Values)
        {
            try
            {
                if (entry.Window.Content is ReaderPage readerPage)
                {
                    readerPage.PauseTransientPlayback();
                }

                if (entry.Window.AppWindow.IsVisible)
                {
                    if (!_bossHiddenWindows.Contains(entry.Window))
                    {
                        _bossHiddenWindows.Add(entry.Window);
                    }

                    entry.Window.AppWindow.Hide();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "老板键隐藏独立阅读窗口失败：{Key}", entry.Key);
            }
        }

        foreach (Window aux in _auxWindows.Values)
        {
            try
            {
                if (aux.AppWindow.IsVisible)
                {
                    if (!_bossHiddenWindows.Contains(aux))
                    {
                        _bossHiddenWindows.Add(aux);
                    }

                    aux.AppWindow.Hide();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "老板键隐藏独立画廊窗口失败");
            }
        }
    }

    /// <summary>Restores detached windows previously hidden by the Boss Key.</summary>
    public void RestoreBossHiddenWindows()
    {
        if (_bossHiddenWindows.Count == 0)
        {
            return;
        }

        foreach (Window win in _bossHiddenWindows.ToArray())
        {
            try
            {
                win.AppWindow.Show();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "恢复被老板键隐藏的辅助窗口失败");
            }
        }

        _bossHiddenWindows.Clear();
    }

    /// <summary>Closes all detached reader and gallery windows and releases their resources.</summary>
    public void CloseAll()
    {
        foreach (ManagedReaderWindow entry in _windows.Values.ToArray())
        {
            try
            {
                entry.Window.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "关闭独立阅读窗口失败：{Key}", entry.Key);
                CloseTrackedEntry(entry.Key);
            }
        }

        foreach (Window aux in _auxWindows.Values.ToArray())
        {
            try
            {
                aux.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "关闭独立画廊窗口失败");
            }
        }
        _auxWindows.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseAll();
    }

    private sealed record ManagedReaderWindow(string Key, Window Window, ReaderViewModel ViewModel);
}

/// <summary>
/// Headless registry verifying duplicate-window suppression and lifecycle tracking independently of
/// WinUI's dispatcher thread so multi-window semantics can be unit-tested reliably.
/// </summary>
internal sealed class WindowKeyRegistry
{
    private readonly Dictionary<string, Guid> _open = new(StringComparer.Ordinal);

    public int Count => _open.Count;

    public bool TryOpen(string key, out Guid windowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_open.TryGetValue(key, out windowId))
        {
            return false;
        }

        windowId = Guid.NewGuid();
        _open[key] = windowId;
        return true;
    }

    public bool Contains(string key) => _open.ContainsKey(key);

    public bool Close(string key) => _open.Remove(key);

    public void Clear() => _open.Clear();
}
