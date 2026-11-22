namespace EhViewer.Core.Client.Data;

public sealed class GalleryTagGroup
{
    public string? GroupName { get; set; }
    private readonly List<string> _tagList = [];

    public void AddTag(string tag) => _tagList.Add(tag);
    public int Size => _tagList.Count;
    public string GetTagAt(int position) => _tagList[position];
    public IReadOnlyList<string> Tags => _tagList;
}
