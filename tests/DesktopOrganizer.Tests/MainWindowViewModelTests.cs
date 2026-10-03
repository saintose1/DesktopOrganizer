using DesktopOrganizer.App.ViewModels;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Core.Storage;
using DesktopOrganizer.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using DesktopOrganizer.Core.Desktop;

namespace DesktopOrganizer.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void AppearancePreviewIsLiveButSavesOneSnapshotForManySliderChanges()
    {
        var store = new FakeStore(); var desktop = new RecordingDesktop();
        using var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance, desktop);
        vm.CreateCommand.Execute(null);
        for (var i = 0; i <= 100; i++) vm.AppearanceEditor.BodyOpacity = i;
        vm.AppearanceEditor.Name = "Live title"; vm.AppearanceEditor.Bold = true;
        Assert.Equal("Live title", Assert.Single(desktop.Last).Name);
        Assert.True(vm.SelectedFence!.Appearance.Bold);
        Assert.Equal(1, store.SaveCount);
        Assert.True(vm.FlushAppearanceChanges());
        Assert.Equal(2, store.SaveCount);
        Assert.Equal(vm.SelectedFence.Appearance, Assert.Single(store.Result.Fences).Appearance);
        Assert.True(vm.FlushAppearanceChanges()); Assert.Equal(2, store.SaveCount);
    }

    [Fact]
    public void InvalidEditorDraftNeverReachesModelAndCanRecover()
    {
        var store = new FakeStore();
        using var vm = ViewModel(store); vm.CreateCommand.Execute(null);
        var original = vm.SelectedFence;
        vm.AppearanceEditor.TextColor = "#FF";
        Assert.True(vm.AppearanceEditor.HasError); Assert.Equal(original, vm.SelectedFence);
        vm.AppearanceEditor.TextColor = "#FF0000";
        Assert.False(vm.AppearanceEditor.HasError); Assert.Equal("#FF0000", vm.SelectedFence!.Appearance.TextColor);
        vm.AppearanceEditor.FontSize = "NaN";
        Assert.True(vm.AppearanceEditor.HasError); Assert.Equal(16, vm.SelectedFence.Appearance.FontSize);
    }

    [Fact]
    public void FailedAppearanceWriteRollsBackDesktopAndModel()
    {
        var store = new FakeStore(); var desktop = new RecordingDesktop();
        using var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance, desktop);
        vm.CreateCommand.Execute(null); var original = vm.SelectedFence;
        vm.AppearanceEditor.HeaderOpacity = 10; vm.AppearanceEditor.Name = "Preview";
        store.FailSave = true;
        Assert.False(vm.FlushAppearanceChanges());
        Assert.Equal(original, vm.SelectedFence); Assert.Equal(original, Assert.Single(desktop.Last));
        Assert.Equal(original, Assert.Single(store.Result.Fences)); Assert.True(vm.HasError);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PendingStyleAndDesktopGestureShareOneCommitOrRollback(bool failSave)
    {
        var store = new FakeStore(); var desktop = new RecordingDesktop();
        using var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance, desktop);
        vm.CreateCommand.Execute(null); var durable = vm.SelectedFence!;
        vm.AppearanceEditor.Italic = true; var styled = vm.SelectedFence!;
        desktop.Gesture(styled, InteractionPhase.Started);
        desktop.Gesture(styled.WithBounds(100, 100, 400, 300), InteractionPhase.Preview);
        vm.FlushAppearanceChanges(); Assert.Equal(1, store.SaveCount);
        store.FailSave = failSave;
        desktop.Gesture(vm.SelectedFence!, InteractionPhase.Completed);
        Assert.Equal(failSave ? durable : vm.SelectedFence, Assert.Single(store.Result.Fences));
        Assert.Equal(failSave ? durable : vm.SelectedFence, Assert.Single(desktop.Last));
        Assert.Equal(failSave ? 1 : 2, store.SaveCount);
    }

    [Fact]
    public void PendingStyleFlushesOnSelectionAndSurvivesLegacyApply()
    {
        var store = new FakeStore();
        using var vm = ViewModel(store); vm.CreateCommand.Execute(null); vm.CreateCommand.Execute(null);
        var first = vm.Fences[0]; var secondId = vm.SelectedFence!.Id;
        vm.AppearanceEditor.BodyColor = "#221133";
        vm.SelectedFence = first;
        Assert.Equal("#221133", store.Result.Fences.Single(f => f.Id == secondId).Appearance.BodyColor);
        vm.SelectedFence = vm.Fences.Single(f => f.Id == secondId);
        vm.X = "200"; vm.ApplyCommand.Execute(null);
        Assert.Equal("#221133", vm.SelectedFence!.Appearance.BodyColor);
    }

    [Fact]
    public void DesktopPreviewUpdatesModelImmediatelyButSavesOnlyOnCompletion()
    {
        var store = new FakeStore();
        var desktop = new RecordingDesktop();
        var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance, desktop);
        vm.CreateCommand.Execute(null);
        var original = vm.Fences[0];
        desktop.Gesture(original, InteractionPhase.Started);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.False(vm.CreateCommand.CanExecute(null));
        for (var i = 1; i <= 200; i++)
            desktop.Gesture(original.WithBounds(i, i, 450, 350), InteractionPhase.Preview);
        Assert.Equal(200, vm.Fences[0].X);
        Assert.Equal(vm.Fences[0], vm.SelectedFence);
        Assert.Equal(original, desktop.Last[0]); // No editor-to-native feedback during a gesture.
        Assert.Equal(1, store.SaveCount);
        desktop.Gesture(vm.Fences[0], InteractionPhase.Completed);
        Assert.Equal(2, store.SaveCount);
        Assert.Equal(vm.Fences[0], store.Result.Fences[0]);
        Assert.Equal(vm.Fences[0], desktop.Last[0]);
        Assert.True(vm.ApplyCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CancelOrWriteFailureRestoresOriginalModelAndSurface(bool failSave)
    {
        var store = new FakeStore();
        var desktop = new RecordingDesktop();
        var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance, desktop);
        vm.CreateCommand.Execute(null);
        var original = vm.Fences[0];
        desktop.Gesture(original, InteractionPhase.Started);
        desktop.Gesture(original.WithBounds(300, 200, 500, 450), InteractionPhase.Preview);
        store.FailSave = failSave;
        desktop.Gesture(vm.Fences[0], failSave ? InteractionPhase.Completed : InteractionPhase.Cancelled);
        Assert.Equal(original, Assert.Single(vm.Fences));
        Assert.Equal(original, Assert.Single(desktop.Last));
        Assert.Equal(original, Assert.Single(store.Result.Fences));
        Assert.Equal(failSave, vm.HasError);
        Assert.True(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void NoMovementDoesNotWriteAndReadOnlyDataRejectsDesktopChanges()
    {
        var original = new Fence(Guid.NewGuid(), "Work", 120, 80, 450, 350);
        var store = new FakeStore { Result = new([original]) };
        var desktop = new RecordingDesktop();
        var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance, desktop);
        desktop.Gesture(original, InteractionPhase.Started);
        desktop.Gesture(original, InteractionPhase.Completed);
        Assert.Equal(0, store.SaveCount);
        var readOnlyDesktop = new RecordingDesktop();
        var readOnly = new MainWindowViewModel(new FakeStore { Result = new([original], "Locked", false) },
            NullLogger<MainWindowViewModel>.Instance, readOnlyDesktop);
        Assert.False(readOnlyDesktop.Enabled);
        readOnlyDesktop.Gesture(original, InteractionPhase.Started);
        readOnlyDesktop.Gesture(original.ToggleCollapsed(), InteractionPhase.Preview);
        readOnlyDesktop.Gesture(original, InteractionPhase.Completed);
        Assert.Equal(original, readOnly.Fences[0]);
    }

    [Fact]
    public void CollapseMoveAndManualRenamePreserveHeightAndStateAcrossRestart()
    {
        using var directory = new TestDirectory();
        var store = new JsonFenceStore(directory.DirectoryPath, NullLogger<JsonFenceStore>.Instance);
        var desktop = new RecordingDesktop();
        var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance, desktop);
        vm.CreateCommand.Execute(null);
        var original = vm.Fences[0];
        desktop.Gesture(original, InteractionPhase.Started);
        desktop.Gesture(original.WithBounds(320, 240, 450, 350).ToggleCollapsed(), InteractionPhase.Preview);
        desktop.Gesture(vm.Fences[0], InteractionPhase.Completed);
        vm.Name = "Renamed";
        vm.ApplyCommand.Execute(null);
        var restored = Assert.Single(ViewModel(store).Fences);
        Assert.True(restored.IsCollapsed);
        Assert.Equal(350, restored.Height);
        Assert.Equal(320, restored.X);
        Assert.Equal(240, restored.Y);
        Assert.Equal("Renamed", restored.Name);
        Assert.Equal(original.Id, restored.Id);
    }

    [Fact]
    public void DesktopReceivesOnlyPersistedSnapshotsForCreateUpdateDelete()
    {
        var store = new FakeStore();
        var desktop = new RecordingDesktop();
        var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance, desktop);
        Assert.Empty(desktop.Last);
        vm.CreateCommand.Execute(null);
        Assert.Single(desktop.Last);
        vm.Name = "Updated";
        vm.Width = "500";
        vm.ApplyCommand.Execute(null);
        Assert.Equal("Updated", Assert.Single(desktop.Last).Name);
        Assert.Equal(500, desktop.Last[0].Width);
        var beforeFailure = desktop.Last;
        store.FailSave = true;
        vm.DeleteCommand.Execute(null);
        Assert.Same(beforeFailure, desktop.Last);
        store.FailSave = false;
        vm.DeleteCommand.Execute(null);
        Assert.Empty(desktop.Last);
    }

    [Fact]
    public void DesktopStatusFollowsConnectionNotifications()
    {
        var desktop = new RecordingDesktop();
        var vm = new MainWindowViewModel(new FakeStore(), NullLogger<MainWindowViewModel>.Instance, desktop);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        desktop.Change(new(true, 2, "Host=0x1234"));
        Assert.Contains("Connected", vm.DesktopStatus);
        Assert.Contains("Surfaces: 2", vm.DesktopStatus);
        Assert.Equal("Host=0x1234", vm.DesktopDiagnostics);
        Assert.Contains(nameof(vm.DesktopStatus), changes);
        desktop.Change(new(false, 0, "Host missing"));
        Assert.Contains("Disconnected", vm.DesktopStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AssignmentCommitsAtomicallyOrKeepsPreviousStateOnWriteFailure(bool failSave)
    {
        var store = new FakeStore(); var desktop = new RecordingDesktop();
        var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance, desktop);
        vm.CreateCommand.Execute(null);
        var original = vm.Fences[0];
        var assigned = original.WithIcons([new DesktopIconAssignment(@"C:\Desktop\A.lnk", "A", 45, 66)]);
        store.FailSave = failSave;
        Assert.Equal(!failSave, desktop.Assign([assigned]));
        Assert.Equal(failSave ? original : assigned, Assert.Single(vm.Fences));
        Assert.Equal(failSave ? original : assigned, Assert.Single(desktop.Last));
        Assert.Equal(failSave ? original : assigned, Assert.Single(store.Result.Fences));
        Assert.Equal(failSave, vm.HasError);
    }

    [Fact]
    public void RenameAndBoundsEditingPreserveAssignments()
    {
        var store = new FakeStore(); var desktop = new RecordingDesktop();
        var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance, desktop);
        vm.CreateCommand.Execute(null);
        var assignments = new[] { new DesktopIconAssignment("A", "A", 45, 66) };
        Assert.True(desktop.Assign([vm.Fences[0].WithIcons(assignments)]));
        vm.Name = "Renamed"; vm.X = "200";
        vm.ApplyCommand.Execute(null);
        Assert.Equal("Renamed", vm.Fences[0].Name);
        Assert.Equal(assignments, vm.Fences[0].Icons.ToArray());
        Assert.Empty(vm.Error);
    }

    [Fact]
    public void AssignmentCannotCommitDuringActiveGestureOrChangeFenceIdentities()
    {
        var store = new FakeStore(); var desktop = new RecordingDesktop();
        var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance, desktop);
        vm.CreateCommand.Execute(null);
        var original = vm.Fences[0];
        Assert.False(desktop.Assign([]));
        Assert.False(desktop.Assign([new Fence(Guid.NewGuid(), "Other", 0, 0, 300, 200)]));
        desktop.Gesture(original, InteractionPhase.Started);
        Assert.False(desktop.Assign([original.WithIcons([new DesktopIconAssignment("A", "A", 45, 66)])]));
        desktop.Gesture(original, InteractionPhase.Cancelled);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(original, Assert.Single(vm.Fences));
    }

    [Fact]
    public void DesktopPreviewPreservesNameWhenListBoxTemporarilyClearsSelection()
    {
        var store = new FakeStore(); var desktop = new RecordingDesktop();
        var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance, desktop);
        vm.CreateCommand.Execute(null);
        var original = vm.Fences[0];
        ((System.Collections.Specialized.INotifyCollectionChanged)vm.Fences).CollectionChanged += (_, _) => vm.SelectedFence = null;
        desktop.Gesture(original, InteractionPhase.Started);
        desktop.Gesture(original.WithBounds(250, 180, 450, 350), InteractionPhase.Preview);
        Assert.Equal(original.Name, vm.Name);
        Assert.Equal(vm.Fences[0], vm.SelectedFence);
        Assert.Equal("250", vm.X);
        Assert.Equal("180", vm.Y);
        desktop.Gesture(vm.Fences[0], InteractionPhase.Completed);
        Assert.Equal(original.Name, vm.Name);
        Assert.Equal(2, store.SaveCount);
    }

    private sealed class RecordingDesktop : IDesktopIntegration
    {
        public DesktopConnection Connection { get; private set; } = new(false, 0, "");
        public IReadOnlyList<Fence> Last { get; private set; } = [];
        public event EventHandler? ConnectionChanged;
        public event EventHandler<FenceInteractionEventArgs>? Interaction;
        public event EventHandler<FenceStateChangeRequest>? StateChangeRequested;
        public Fence NormalizeFence(Fence original, Fence proposed) => proposed;
        public bool Assign(IReadOnlyList<Fence> fences)
        {
            var request = new FenceStateChangeRequest(fences); StateChangeRequested?.Invoke(this, request); return request.Accepted;
        }
        public bool Enabled { get; private set; }
        public bool IsPresentingTransition { get; private set; }
        public void Transition(bool busy) { IsPresentingTransition = busy; ConnectionChanged?.Invoke(this, EventArgs.Empty); }
        public void SetInteractionEnabled(bool enabled) => Enabled = enabled;
        public void Gesture(Fence fence, InteractionPhase phase) => Interaction?.Invoke(this, new(fence, phase));
        public void SetFences(IReadOnlyList<Fence> fences) => Last = fences;
        public void ShowDesktop() { }
        public void Change(DesktopConnection state) { Connection = state; ConnectionChanged?.Invoke(this, EventArgs.Empty); }
    }
    private static MainWindowViewModel ViewModel(IFenceStore store)
        => new(store, NullLogger<MainWindowViewModel>.Instance);

    [Fact]
    public void NativeVisualTransitionBlocksConflictingEditorCommandsButCanCommitItsResult()
    {
        var store = new FakeStore(); var desktop = new RecordingDesktop();
        var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance, desktop);
        vm.CreateCommand.Execute(null); var original = vm.Fences[0];
        desktop.Transition(true);
        Assert.False(vm.CanEdit); Assert.False(vm.CreateCommand.CanExecute(null));
        Assert.False(vm.ApplyCommand.CanExecute(null)); Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.True(desktop.Assign([original.ToggleCollapsed()]));
        desktop.Transition(false);
        Assert.True(vm.CanEdit); Assert.True(vm.Fences[0].IsCollapsed);
        Assert.Equal(2, store.SaveCount);
    }

    [Fact]
    public void CreateRenameMoveResizeDeleteAndRestartPreserveState()
    {
        using var directory = new TestDirectory();
        var store = new JsonFenceStore(directory.DirectoryPath, NullLogger<JsonFenceStore>.Instance);
        var vm = ViewModel(store);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.False(vm.DeleteCommand.CanExecute(null));
        vm.CreateCommand.Execute(null);
        vm.CreateCommand.Execute(null);
        Assert.Equal(2, vm.Fences.Count);
        Assert.NotEqual(vm.Fences[0].Id, vm.Fences[1].Id);
        var id = vm.SelectedFence!.Id;
        vm.Name = "  Работа  ";
        vm.X = "120.5"; vm.Y = "-40"; vm.Width = "450"; vm.Height = "350";
        vm.ApplyCommand.Execute(null);
        Assert.Empty(vm.Error);
        var expected = new Fence(id, "Работа", 120.5, -40, 450, 350);
        Assert.Equal(expected, vm.SelectedFence);
        vm.SelectedFence = vm.Fences[0];
        vm.DeleteCommand.Execute(null);
        var restarted = ViewModel(new JsonFenceStore(directory.DirectoryPath, NullLogger<JsonFenceStore>.Instance));
        Assert.Equal(expected, Assert.Single(restarted.Fences));
        Assert.Equal(expected, restarted.SelectedFence);
        restarted.DeleteCommand.Execute(null);
        Assert.Empty(ViewModel(store).Fences);
        Assert.Null(restarted.SelectedFence);
        Assert.False(restarted.DeleteCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("", "0", "300", "200")]
    [InlineData("Valid", "abc", "300", "200")]
    [InlineData("Valid", "NaN", "300", "200")]
    [InlineData("Valid", "0", "0", "200")]
    [InlineData("Valid", "0", "300", "-1")]
    public void InvalidDraftDoesNotChangeOrSaveState(string name, string x, string width, string height)
    {
        var store = new FakeStore();
        var vm = ViewModel(store);
        vm.CreateCommand.Execute(null);
        var original = vm.SelectedFence;
        vm.Name = name; vm.X = x; vm.Width = width; vm.Height = height;
        vm.ApplyCommand.Execute(null);
        Assert.True(vm.HasError);
        Assert.Equal(original, Assert.Single(vm.Fences));
        Assert.Equal(1, store.SaveCount);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("apply")]
    [InlineData("delete")]
    public void WriteFailureKeepsExistingStateAndShowsError(string command)
    {
        var store = new FakeStore();
        var vm = ViewModel(store);
        vm.CreateCommand.Execute(null);
        var original = vm.SelectedFence;
        store.FailSave = true;
        vm.Name = "Updated";
        switch (command)
        {
            case "create": vm.CreateCommand.Execute(null); break;
            case "apply": vm.ApplyCommand.Execute(null); break;
            case "delete": vm.DeleteCommand.Execute(null); break;
        }
        Assert.Equal(original, Assert.Single(vm.Fences));
        Assert.Equal(original, vm.SelectedFence);
        Assert.True(vm.HasError);
    }

    [Fact]
    public void ReadFailureDisablesAllMutationCommands()
    {
        var vm = ViewModel(new FakeStore { Result = new FenceLoadResult([], "Cannot read", false) });
        Assert.True(vm.HasLoadWarning);
        Assert.False(vm.CreateCommand.CanExecute(null));
        vm.CreateCommand.Execute(null);
        Assert.Empty(vm.Fences);
    }

    [Fact]
    public void CorruptionWarningDoesNotPreventCreatingNewGroups()
    {
        var vm = ViewModel(new FakeStore { Result = new FenceLoadResult([], "Recovered") });
        Assert.True(vm.HasLoadWarning);
        vm.CreateCommand.Execute(null);
        Assert.Single(vm.Fences);
    }

    [Fact]
    public void SelectionUpdatesEditorAndCommandAvailability()
    {
        var vm = ViewModel(new FakeStore());
        var changes = new List<string?>();
        var commandChanges = 0;
        vm.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        vm.ApplyCommand.CanExecuteChanged += (_, _) => commandChanges++;
        vm.CreateCommand.Execute(null);
        Assert.Equal("Группа 1", vm.Name);
        Assert.True(vm.ApplyCommand.CanExecute(null));
        Assert.Contains(nameof(vm.Name), changes);
        Assert.Contains(nameof(vm.CanEdit), changes);
        Assert.True(commandChanges > 0);
    }

    private sealed class FakeStore : IFenceStore
    {
        public bool FailSave { get; set; }
        public int SaveCount { get; private set; }
        public FenceLoadResult Result { get; set; } = new([]);
        public FenceLoadResult Load() => Result;
        public void Save(IReadOnlyCollection<Fence> fences)
        {
            if (FailSave) throw new FenceStorageException("Test write error", new IOException());
            SaveCount++;
            Result = new FenceLoadResult(fences.ToArray());
        }
    }
}
