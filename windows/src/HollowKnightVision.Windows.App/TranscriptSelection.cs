using System.Windows.Controls;

namespace HollowKnightVision.Windows.App;

internal static class TranscriptSelection
{
    internal static void UpdateTranscript(this TextBox box, string text)
    {
        var old = box.Text;
        if (old == text) return;
        var start = box.SelectionStart;
        var selected = box.SelectedText;
        box.Text = text;
        if (selected.Length == 0) return;
        // The notebook normally prepends new entries. Use the whole old transcript
        // to distinguish identical readings from different entries.
        if (text.EndsWith(old, StringComparison.Ordinal))
            start += text.Length - old.Length;
        else if (!text.StartsWith(old, StringComparison.Ordinal))
        {
            start = text.IndexOf(selected, StringComparison.Ordinal);
            if (start < 0 || start != text.LastIndexOf(selected, StringComparison.Ordinal)) return;
        }
        box.Select(start, selected.Length);
    }

    internal static void CopyTranscript(this TextBox box)
    {
        var start = box.SelectionStart;
        var length = box.SelectionLength;
        try
        {
            box.SelectAll();
            box.Copy();
        }
        finally { box.Select(start, length); }
    }
}
