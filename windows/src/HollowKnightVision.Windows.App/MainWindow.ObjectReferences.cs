using System.IO;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private ObjectReferencePanel? objectReferencePanel;
    private void RefreshObjectReferences()
    {
        var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(ExamplesRootText.Text)))!;
        if (objectReferencePanel is null || !string.Equals(objectReferencePanel.RootDirectory, root, StringComparison.OrdinalIgnoreCase))
        {
            objectReferencePanel?.StopAndDrain();
            objectReferencePanel = new(root, key => initialization.Text(key), SelectModelClass, example =>
            {
                var selected = objectReferencePanel?.SelectedClass ?? ClassIdentifierText.Text;
                if (!SelectWorkspace("Label", capture: false)) return;
                try { ApplySavedLabelExample(example, selected); LabelDeleteButton.IsChecked = true; }
                catch (Exception error) { ShowError(error); }
            });
            objectReferencePanel.AtlasChanged += atlas => { screenshotReviewPanel?.SetAtlas(atlas); modelSummaryReviewPanel?.SetAtlas(atlas); };
        }
        var classes = LabelingObjectReferencePolicy.ModelClasses(customObjectChoices.Select(item => new MenuStencilLabel(item.Identifier, item.Name)).ToArray());
        objectReferencePanel.SetExamples(LabelingScreenshotReviewSession.ModelExamples(examples), classes, ClassIdentifierText.Text);
        if (!ModelObjectsPanel.Children.Contains(objectReferencePanel))
        { ModelObjectsPanel.Children.Clear(); ModelObjectsPanel.Children.Add(objectReferencePanel); }
    }
}
