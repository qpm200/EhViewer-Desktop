using System.Text;

namespace EhViewer.Core.Client.Data.UserTag;

public sealed class UserTag
{
    public string? UserTagId { get; set; }
    public string? TagName { get; set; }
    public bool Watched { get; set; }
    public bool Hidden { get; set; }
    public string? Color { get; set; }
    public int TagWeight { get; set; }

    public long GetId()
    {
        if (string.IsNullOrEmpty(UserTagId) || UserTagId.Length <= 8)
        {
            return 0;
        }
        return long.Parse(UserTagId.AsSpan(8));
    }

    public string DeleteParam()
    {
        return $"usertag_action=mass&tagname_new=&tagcolor_new=&tagweight_new={TagWeight}&modify_usertags%5B%5D={GetId()}&usertag_target=0";
    }
}

public sealed class UserTagList
{
    public List<UserTag> UserTags { get; set; } = [];
    public int StageId { get; set; }

    public UserTag this[int index] => UserTags[index];
    public int Count => UserTags.Count;
}

public sealed class TagPushParam
{
    public string? UserTagAction { get; set; }
    public string? TagNameNew { get; set; }
    public string? TagWatchNew { get; set; }
    public string? TagHiddenNew { get; set; }
    public string? TagColorNew { get; set; }
    public int TagWeightNew { get; set; }
    public int UserTagTarget { get; set; }

    /// <summary>
    /// Builds form-encoded payload for adding a user tag (fixes upstream Java immutability bug
    /// where <c>tagName.replace(...)</c> return value was discarded).
    /// </summary>
    public string AddTagParam()
    {
        string state = string.Empty;
        if (string.Equals(TagHiddenNew, "on", StringComparison.Ordinal))
        {
            state = "&taghide_new=on";
        }
        if (string.Equals(TagWatchNew, "on", StringComparison.Ordinal))
        {
            state = "&tagwatch_new=on";
        }

        return $"usertag_action=add&tagname_new={EncodeTagName()}{state}&tagcolor_new={EncodeColorName()}&tagweight_new=10&usertag_target=0";
    }

    private string EncodeTagName()
    {
        return (TagNameNew ?? string.Empty).Replace(":", "%3A", StringComparison.Ordinal).Replace(" ", "+", StringComparison.Ordinal);
    }

    private string EncodeColorName()
    {
        return string.IsNullOrEmpty(TagColorNew) ? string.Empty : TagColorNew.Replace("#", "%23", StringComparison.Ordinal);
    }
}

public sealed class TagEntry
{
    public string English { get; set; }
    public string Chinese { get; set; }

    public TagEntry(string content)
    {
        string[] parts = content.Split('\r');
        Chinese = parts.Length > 1 ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])) : string.Empty;
        English = content;
    }

    public TagEntry(string english, string chinese)
    {
        English = english;
        Chinese = chinese;
    }

    public bool Involve(string chars) =>
        English.Contains(chars, StringComparison.OrdinalIgnoreCase) ||
        Chinese.Contains(chars, StringComparison.OrdinalIgnoreCase);
}
