namespace EhViewer.Core.Client.Data;

/// <summary>
/// Bitmask flags for E-Hentai advanced search options (<c>f_*</c> query parameters),
/// matching upstream <c>AdvanceSearchTable</c>.
/// </summary>
[Flags]
public enum AdvanceSearchFlags
{
    None = 0,
    SName = 0x1,
    STags = 0x2,
    SDesc = 0x4,
    STorr = 0x8,
    STo = 0x10,
    SDt1 = 0x20,
    SDt2 = 0x40,
    SH = 0x80,
    SFl = 0x100,
    SFu = 0x200,
    SFt = 0x400
}
