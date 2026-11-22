namespace EhViewer.Core.Client.Data;

public sealed class GalleryPreview
{
    public string? ImageKey { get; set; }
    public string? ImageUrl { get; set; }
    public string? PageUrl { get; set; }
    public int Position { get; set; }
    public int OffsetX { get; set; } = int.MinValue;
    public int OffsetY { get; set; } = int.MinValue;
    public int ClipWidth { get; set; } = int.MinValue;
    public int ClipHeight { get; set; } = int.MinValue;
}

public abstract class PreviewSet
{
    public abstract int Size { get; }
    public abstract int GetPosition(int index);
    public abstract string? GetPageUrlAt(int index);
    public abstract GalleryPreview GetGalleryPreview(long gid, int index);
}

public sealed class LargePreviewSet : PreviewSet
{
    private readonly List<int> _positionList = [];
    private readonly List<string> _imageUrlList = [];
    private readonly List<string> _pageUrlList = [];

    public void AddItem(int index, string imageUrl, string pageUrl)
    {
        _positionList.Add(index);
        _imageUrlList.Add(imageUrl);
        _pageUrlList.Add(pageUrl);
    }

    public override int Size => _imageUrlList.Count;

    public override int GetPosition(int index) => _positionList[index];

    public string GetImageUrlAt(int index) => _imageUrlList[index];

    public override string GetPageUrlAt(int index) => _pageUrlList[index];

    public override GalleryPreview GetGalleryPreview(long gid, int index)
    {
        int position = _positionList[index];
        return new GalleryPreview
        {
            Position = position,
            ImageKey = EhCacheKeyFactory.GetLargePreviewKey(gid, position),
            ImageUrl = _imageUrlList[index],
            PageUrl = _pageUrlList[index]
        };
    }
}

public sealed class NormalPreviewSet : PreviewSet
{
    private readonly List<int> _positionList = [];
    private readonly List<string> _imageKeyList = [];
    private readonly List<string> _imageUrlList = [];
    private readonly List<int> _offsetXList = [];
    private readonly List<int> _offsetYList = [];
    private readonly List<int> _clipWidthList = [];
    private readonly List<int> _clipHeightList = [];
    private readonly List<string> _pageUrlList = [];

    private static string ComputeImageKey(string imageUrl)
    {
        int index = imageUrl.IndexOf('/');
        return index >= 0 ? imageUrl[(index + 1)..] : imageUrl;
    }

    public void AddItem(int position, string imageUrl, int xOffset, int yOffset, int width, int height, string pageUrl)
    {
        _positionList.Add(position);
        _imageKeyList.Add(ComputeImageKey(imageUrl));
        _imageUrlList.Add(imageUrl);
        _offsetXList.Add(xOffset);
        _offsetYList.Add(yOffset);
        _clipWidthList.Add(width);
        _clipHeightList.Add(height);
        _pageUrlList.Add(pageUrl);
    }

    public override int Size => _positionList.Count;

    public override int GetPosition(int index) => _positionList[index];

    public string GetImageKeyAt(int index) => _imageKeyList[index];

    public string GetImageUrlAt(int index) => _imageUrlList[index];

    public int GetOffsetXAt(int index) => _offsetXList[index];

    public int GetOffsetYAt(int index) => _offsetYList[index];

    public int GetClipWidthAt(int index) => _clipWidthList[index];

    public int GetClipHeightAt(int index) => _clipHeightList[index];

    public override string GetPageUrlAt(int index) => _pageUrlList[index];

    public override GalleryPreview GetGalleryPreview(long gid, int index)
    {
        return new GalleryPreview
        {
            Position = _positionList[index],
            ImageKey = _imageKeyList[index],
            ImageUrl = _imageUrlList[index],
            PageUrl = _pageUrlList[index],
            OffsetX = _offsetXList[index],
            OffsetY = _offsetYList[index],
            ClipWidth = _clipWidthList[index],
            ClipHeight = _clipHeightList[index]
        };
    }
}
