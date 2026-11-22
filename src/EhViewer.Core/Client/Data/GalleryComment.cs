namespace EhViewer.Core.Client.Data;

public sealed class GalleryComment
{
    public long Id { get; set; }
    public int Score { get; set; }
    public bool Editable { get; set; }
    public bool VoteUpAble { get; set; }
    public bool VoteUpEd { get; set; }
    public bool VoteDownAble { get; set; }
    public bool VoteDownEd { get; set; }
    public string? VoteState { get; set; }
    public long Time { get; set; }
    public string? User { get; set; }
    public string? Comment { get; set; }
    public long LastEdited { get; set; }
}

public sealed class GalleryCommentList
{
    public GalleryComment[]? Comments { get; set; }
    public bool HasMore { get; set; }

    public GalleryCommentList()
    {
    }

    public GalleryCommentList(GalleryComment[]? comments, bool hasMore)
    {
        Comments = comments;
        HasMore = hasMore;
    }
}
