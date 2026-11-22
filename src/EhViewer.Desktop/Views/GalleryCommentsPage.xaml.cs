using System.Collections.Specialized;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Parser;
using EhViewer.Core.Storage;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EhViewer.Desktop.Views;

/// <summary>
/// Dedicated gallery comments scene mirroring Android GalleryCommentsScene:
/// full thread, upvote/downvote, reply/quote, inline comment edit, link opening, and commenter blacklist.
/// </summary>
public sealed partial class GalleryCommentsPage : Page, ISceneAware
{
    private readonly GalleryDetailViewModel _viewModel;
    private readonly GalleryDetailActions _actions;
    private IShellCoordinator? _shell;
    private long? _editingCommentId;

    public GalleryCommentsPage(
        GalleryDetailViewModel viewModel,
        GalleryDetailActions actions,
        IShellCoordinator? shell = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(actions);

        _viewModel = viewModel;
        _actions = actions;
        _shell = shell;

        InitializeComponent();

        CommentsRepeater.ItemsSource = _viewModel.Comments;
        _viewModel.Comments.CollectionChanged += OnCommentsCollectionChanged;
        Unloaded += (_, _) => _viewModel.Comments.CollectionChanged -= OnCommentsCollectionChanged;
        UpdateHeader();
    }

    public void AttachCoordinator(IShellCoordinator coordinator) => _shell = coordinator;

    private void OnCommentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => UpdateHeader();

    private void UpdateHeader()
    {
        Toolbar.Subtitle = $"{_viewModel.Title} · 共 {_viewModel.Comments.Count} 条可见评论";
        EmptyCommentsText.Visibility = _viewModel.Comments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBackRequested(object sender, RoutedEventArgs e) => _shell?.GoBack();

    private async void OnRefreshCommentsClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Detail is not { } detail || _shell is null)
        {
            return;
        }

        try
        {
            _shell.SetStatus("正在刷新评论...");
            GalleryDetail refreshed = await _shell.Services
                .GetRequiredService<GalleryBrowseService>()
                .GetDetailAsync(detail, _shell.LifetimeToken)
                .ConfigureAwait(true);

            if (refreshed.Comments is not null)
            {
                _viewModel.UpdateComments(refreshed.Comments);
                _shell.SetStatus($"评论已刷新（共 {_viewModel.Comments.Count} 条）。");
            }
        }
        catch (Exception ex)
        {
            _shell.SetStatus($"刷新评论失败：{GalleryBrowseService.DescribeFailure(ex)}");
        }
    }

    private async void OnSubmitCommentClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Detail is not { } detail)
        {
            return;
        }

        string text = CommentInputBox.Text.Trim();
        if (text.Length == 0)
        {
            _shell?.SetStatus("请先输入评论内容。");
            return;
        }

        SubmitCommentBtn.IsEnabled = false;
        try
        {
            GalleryCommentList updated = _editingCommentId is { } editId
                ? await _actions.EditCommentAsync(detail, editId, text).ConfigureAwait(true)
                : await _actions.CommentAsync(detail, text).ConfigureAwait(true);

            _viewModel.UpdateComments(updated);
            CancelEditingState();
            CommentInputBox.Text = string.Empty;
            _shell?.SetStatus(_editingCommentId is not null ? "评论已修改。" : "评论已发送。");
        }
        catch (Exception ex)
        {
            _shell?.SetStatus($"发送失败：{GalleryBrowseService.DescribeFailure(ex)}");
        }
        finally
        {
            SubmitCommentBtn.IsEnabled = true;
        }
    }

    private void OnReplyCommentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CommentViewModel comment })
        {
            return;
        }

        CancelEditingState();
        string preview = comment.Body.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').FirstOrDefault() ?? string.Empty;
        if (preview.Length > 72)
        {
            preview = preview[..72] + "...";
        }

        string quote = $"@{comment.User} > {preview}\n";
        if (!CommentInputBox.Text.Contains(quote, StringComparison.Ordinal))
        {
            CommentInputBox.Text = quote + CommentInputBox.Text;
        }

        CommentInputBox.Focus(FocusState.Programmatic);
        CommentInputBox.SelectionStart = CommentInputBox.Text.Length;
    }

    private async void OnVoteUpClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Detail is { } detail && sender is FrameworkElement { Tag: CommentViewModel comment })
        {
            await VoteAsync(detail, comment, 1).ConfigureAwait(true);
        }
    }

    private async void OnVoteDownClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Detail is { } detail && sender is FrameworkElement { Tag: CommentViewModel comment })
        {
            await VoteAsync(detail, comment, -1).ConfigureAwait(true);
        }
    }

    private async Task VoteAsync(GalleryDetail detail, CommentViewModel comment, int vote)
    {
        try
        {
            VoteCommentParser.Result result = await _actions.VoteCommentAsync(detail, comment.Id, vote).ConfigureAwait(true);
            _viewModel.ApplyCommentVote(result);
            _shell?.SetStatus("已提交评论投票。");
        }
        catch (Exception ex)
        {
            _shell?.SetStatus($"投票失败：{GalleryBrowseService.DescribeFailure(ex)}");
        }
    }

    private async void OnEditCommentClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Detail is not { } detail || sender is not FrameworkElement { Tag: CommentViewModel comment })
        {
            return;
        }

        try
        {
            GetEditCommentParser.Result editable = await _actions.GetEditCommentAsync(detail, comment.Id).ConfigureAwait(true);
            _editingCommentId = editable.Id;
            EditingBanner.Visibility = Visibility.Visible;
            EditingBannerText.Text = $"正在编辑评论 #{editable.Id}";
            SubmitCommentBtn.Content = "保存修改";
            CommentInputBox.Text = editable.Comment;
            CommentInputBox.Focus(FocusState.Programmatic);
            CommentInputBox.SelectionStart = CommentInputBox.Text.Length;
        }
        catch (Exception ex)
        {
            _shell?.SetStatus($"无法载入编辑内容：{GalleryBrowseService.DescribeFailure(ex)}");
        }
    }

    private void OnCancelEditClick(object sender, RoutedEventArgs e)
    {
        CancelEditingState();
        CommentInputBox.Text = string.Empty;
    }

    private void CancelEditingState()
    {
        _editingCommentId = null;
        EditingBanner.Visibility = Visibility.Collapsed;
        SubmitCommentBtn.Content = "发送评论";
    }

    private void OnCommentLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && !string.IsNullOrWhiteSpace(url))
        {
            _ = _actions.OpenUrlAsync(url);
        }
    }

    private void OnCommentAuthorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CommentViewModel comment } anchor)
        {
            return;
        }

        var flyout = new MenuFlyout();

        var copyUser = new MenuFlyoutItem { Text = $"复制用户名 ({comment.User})", Icon = new FontIcon { Glyph = "\uE8C8" } };
        copyUser.Click += (_, _) =>
        {
            ClipboardService.CopyText(comment.User);
            _shell?.SetStatus($"已复制用户名：{comment.User}");
        };
        flyout.Items.Add(copyUser);

        var searchUser = new MenuFlyoutItem { Text = $"搜索上传者 {comment.User}", Icon = new FontIcon { Glyph = "\uE721" } };
        searchUser.Click += (_, _) =>
        {
            if (_shell is not null)
            {
                _ = _shell.OpenSearchKeywordAsync($"uploader:\"{comment.User}\"");
            }
        };
        flyout.Items.Add(searchUser);

        if (_shell is not null && !string.IsNullOrWhiteSpace(comment.User) && comment.User != "(匿名)")
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var blockItem = new MenuFlyoutItem { Text = $"屏蔽该评论者 ({comment.User})", Icon = new FontIcon { Glyph = "\uE72E" } };
            blockItem.Click += (_, _) => BlockCommenter(comment.User);
            flyout.Items.Add(blockItem);
        }

        flyout.ShowAt(anchor);
    }

    private void BlockCommenter(string user)
    {
        if (_shell is null)
        {
            return;
        }

        var db = _shell.Services.GetRequiredService<EhDatabase>();
        if (!db.InBlackList(user))
        {
            db.InsertBlackList(new BlackListEntry
            {
                BadGayName = user,
                Reason = "评论区屏蔽",
                AddTime = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                Mode = 1
            });
        }

        for (int i = _viewModel.Comments.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_viewModel.Comments[i].User, user, StringComparison.Ordinal))
            {
                _viewModel.Comments.RemoveAt(i);
            }
        }

        _shell.SetStatus($"已将评论者 {user} 加入黑名单。");
    }
}
