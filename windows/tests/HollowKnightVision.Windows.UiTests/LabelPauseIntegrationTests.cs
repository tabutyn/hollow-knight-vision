using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class LabelPauseIntegrationTests
{
    internal static void Run()
    {
        // Only presentation controls: constructing MainWindow would open user stores and launch the game.
        var editor = new Grid(); var label = new RadioButton(); var capture = new LabelCaptureSession();
        var present = typeof(MainWindow).GetMethod("PresentLabelPauseControls", BindingFlags.Static | BindingFlags.NonPublic)!;
        void Apply() => present.Invoke(null, [editor, label, capture, false]);
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        Apply(); Check(!editor.IsEnabled && !label.IsEnabled && label.ToolTip is string, "Unavailable pause exposes the editor.");
        capture.Apply(GamePauseState.Ready); Apply();
        Check(!editor.IsEnabled && !label.IsEnabled, "Label starts without a clean capture.");
        capture.Accept(new BgraFrame(2, 2, 8, new byte[16], 1, DateTimeOffset.UnixEpoch), capture.Revision); Apply();
        Check(label.IsEnabled && !editor.IsEnabled && label.ToolTip is null, "Ready capture cannot enter labeling.");
        capture.Apply(GamePauseState.Pausing); Apply();
        Check(!label.IsEnabled && !editor.IsEnabled && (label.ToolTip as string)?.Contains("confirm") == true, "Editor enabled before pause acknowledgement.");
        capture.Apply(GamePauseState.Paused); Apply();
        Check(editor.IsEnabled && label.IsEnabled, "Confirmed frozen capture cannot be labeled.");
        capture.Apply(GamePauseState.Resuming); Apply();
        Check(!editor.IsEnabled && !label.IsEnabled && (label.ToolTip as string)?.Contains("resume") == true, "Resuming game still permits edits.");
        capture.Apply(GamePauseState.Ready); Apply();
        present.Invoke(null, [editor, label, capture, true]);
        Check(!label.IsEnabled && (label.ToolTip as string)?.Contains("recording") == true, "Recording permits a label pause.");
        Console.WriteLine("PASS Label controls wait for clean capture and confirmed pause, and disable during resume/recording");
    }
}
