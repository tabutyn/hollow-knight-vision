using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HollowKnightVision.Windows.Core;
using HollowKnightVision.Windows.App;
using System.Windows.Input;

internal static class LabelSelectionIntegrationTests
{
    public static void Run()
    {
        TestProgrammaticSelectors();
        TestAutosaveRefresh();
        TestFailedContextRead();
        TestExitShortcutBeforeTypeAhead();
        Console.WriteLine("PASS WPF label selector transactions, autosave selection and failed-load recovery");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void WithControls(Action<ComboBox, ComboBox, ListBox, Border> test)
    {
        var context = new ComboBox(); var objects = new ComboBox(); var examples = new ListBox();
        var preview = new Border(); var content = new StackPanel();
        content.Children.Add(context); content.Children.Add(objects); content.Children.Add(examples); content.Children.Add(preview);
        var window = new Window { Content = content, Width = 400, Height = 300,
            Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); Pump();
        try { test(context, objects, examples, preview); }
        finally { window.Close(); }
    }

    private static void TestProgrammaticSelectors() => WithControls((context, objects, _, _) =>
    {
        var guard = new LabelingSelectionUpdateGuard(); var resets = 0; var reclassifications = 0;
        string? selectedContext = null; string? selectedObject = null;
        context.SelectionChanged += (_, _) =>
        {
            if (guard.IsUpdating) return;
            resets++; selectedContext = context.SelectedItem as string;
        };
        objects.SelectionChanged += (_, _) =>
        {
            if (guard.IsUpdating) return;
            reclassifications++; selectedObject = objects.SelectedItem as string;
        };
        guard.Run(() =>
        {
            context.ItemsSource = new[] { "game", "main-title", "options" };
            context.SelectedItem = "main-title";
            guard.Run(() =>
            {
                objects.ItemsSource = new[] { "shared.options", "shared.select-decoration" };
                objects.SelectedItem = "shared.options";
            });
            context.SelectedItem = "options";
            objects.ItemsSource = new[] { "shared.audio", "shared.video" };
            objects.SelectedItem = "shared.audio";
        });
        Pump();
        Check(resets == 0 && reclassifications == 0 && !guard.IsUpdating,
            "Loading editor state triggered user-only context reset or box reclassification.");
        context.SelectedItem = "game"; objects.SelectedItem = "shared.video"; Pump();
        Check(resets == 1 && selectedContext == "game" && reclassifications == 1 && selectedObject == "shared.video",
            "Programmatic loading suppressed a subsequent user selection.");
    });

    private static void TestExitShortcutBeforeTypeAhead() => WithControls((context, objects, _, _) =>
    {
        foreach (var selector in new[] { context, objects })
        {
            selector.ItemsSource = new[] { "Knight", "Lifeblood Cocoon" }; selector.SelectedIndex = 0;
            var exits = 0;
            selector.PreviewKeyDown += (_, e) =>
            {
                if (LabelSelectorShortcut.TryExit(selector, e.Key, e.IsRepeat, Keyboard.Modifiers, () => exits++)) e.Handled = true;
            };
            selector.IsDropDownOpen = true; Pump();
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(selector), 0, Key.L)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            selector.RaiseEvent(key); Pump();
            Check(key.Handled && exits == 1 && selector.SelectedIndex == 0 && !selector.IsDropDownOpen,
                "Label exit was lost to dropdown type-ahead or left its popup open.");
            Check(LabelSelectorShortcut.TryExit(selector, Key.L, true, ModifierKeys.None, () => exits++) && exits == 1,
                "Held L repeated the exit action.");
            Check(!LabelSelectorShortcut.TryExit(selector, Key.L, false, ModifierKeys.Control, () => exits++) && exits == 1,
                "Modified key was consumed.");
            selector.IsEditable = true;
            Check(!LabelSelectorShortcut.TryExit(selector, Key.L, false, ModifierKeys.None, () => exits++) && exits == 1,
                "Editable text input lost its L key.");
        }
    });

    private sealed record Example(Guid Id, int Revision);

    private static void TestAutosaveRefresh() => WithControls((_, _, list, preview) =>
    {
        var guard = new LabelingSelectionUpdateGuard(); var examples = new ObservableCollection<Example>();
        var first = new Example(Guid.NewGuid(), 0); var second = new Example(Guid.NewGuid(), 0);
        examples.Add(first); examples.Add(second); list.ItemsSource = examples;
        var opens = 0; var undo = new Stack<int>();
        list.SelectionChanged += (_, _) =>
        {
            if (guard.IsUpdating || list.SelectedItem is not Example) return;
            opens++; undo.Clear(); preview.RenderTransform = Transform.Identity;
        };
        list.SelectedItem = first;
        var transform = new MatrixTransform(new Matrix(2, 0, 0, 2, -123, 84));
        preview.RenderTransform = transform; undo.Push(1); undo.Push(2);
        for (var revision = 1; revision <= 3; revision++)
        {
            var saved = first with { Revision = revision };
            guard.Run(() =>
            {
                examples.Clear(); examples.Add(saved); examples.Add(second);
                list.SelectedItem = examples.First(example => example.Id == saved.Id);
            });
            Pump();
            Check(opens == 1 && undo.Count == 2 && ReferenceEquals(preview.RenderTransform, transform)
                && ((Example)list.SelectedItem).Revision == revision,
                "Autosave reopened the screenshot, discarded undo/zoom, or failed to refresh its selected record.");
        }
        list.SelectedItem = second; Pump();
        Check(opens == 2 && undo.Count == 0 && preview.RenderTransform.Value.IsIdentity,
            "Explicitly opening another saved screenshot no longer resets editor state.");
    });

    private static void TestFailedContextRead() => WithControls((context, _, _, _) =>
    {
        var guard = new LabelingSelectionUpdateGuard();
        context.ItemsSource = new[] { "main-title", "options", "audio" };
        context.SelectedItem = "main-title";
        var activeContext = "main-title"; var attempted = 0;
        context.SelectionChanged += (_, e) =>
        {
            if (guard.IsUpdating) return;
            attempted++;
            try
            {
                // Production loads the bitmap before mutating the active draft.
                if ((string)context.SelectedItem == "options") throw new IOException("missing isolated reference");
                activeContext = (string)context.SelectedItem;
            }
            catch (IOException)
            { guard.Run(() => context.SelectedItem = e.RemovedItems[0]); }
        };
        context.SelectedItem = "options"; Pump();
        Check(activeContext == "main-title" && (string)context.SelectedItem == activeContext && attempted == 1,
            "A failed reference read left the previous image under a different context or recursively reset it.");
        context.SelectedItem = "audio"; Pump();
        Check(activeContext == "audio" && attempted == 2 && !guard.IsUpdating,
            "Failed context recovery disabled later user selections.");
    });
}
