#nullable enable
using Godot;
using OpenTab.Music;

public partial class DrumScorePresenter
{
    // These actions edit the shared section grid; the caller exposes them only for the lead track.
    private void BindMeasurePlacementActions(Control header, ScoreSection section, int measureIndex)
    {
        void Apply(bool duplicate, bool before)
        {
            SetFocusedSection(section);
            if (duplicate) _score.DuplicateMeasure(section, measureIndex, before);
            else InitializeMeasureSnapDefaults(_score.InsertMeasureBefore(section, measureIndex));
            NotifyScoreEdited();
            RenderSection();
        }
        header.GetNode<Button>("HeaderContent/InsertBeforeButton").Pressed += () => Apply(false, true);
        header.GetNode<Button>("HeaderContent/DuplicateBeforeButton").Pressed += () => Apply(true, true);
        header.GetNode<Button>("HeaderContent/DuplicateAfterButton").Pressed += () => Apply(true, false);
    }
}
