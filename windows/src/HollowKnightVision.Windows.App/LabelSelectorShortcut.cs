using System.Windows.Controls;
using System.Windows.Input;

namespace HollowKnightVision.Windows.App;

internal static class LabelSelectorShortcut
{
    internal static bool TryExit(ComboBox selector, Key key, bool repeat, ModifierKeys modifiers, Action exit)
    {
        if (selector.IsEditable || key != Key.L || modifiers != ModifierKeys.None) return false;
        selector.IsDropDownOpen = false;
        if (!repeat) exit();
        return true;
    }
}
