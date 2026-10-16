using System.Globalization;
using System.Text;

namespace Kaku.Core.Text;

/// <summary>Folding for accent/case-insensitive comparisons ("Kōtō" ≈ "koto", "ÁRBOL" ≈ "arbol").</summary>
public static class TextNormalizer
{
    public static string Fold(string s)
    {
        if (s.Length == 0) return s;
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            // keep kana voicing marks (が must not fold to か)
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark && c is not ('゙' or '゚')) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
