namespace BossCam.Tests;

public sealed class OperatorRuntimeRepairTests
{
    [Fact]
    public void MainWindowXaml_Defines_Readable_Dark_Theme_Styles()
    {
        var xaml = ReadRepoFile("src", "BossCam.Desktop", "MainWindow.xaml");

        Assert.Contains("<Style TargetType=\"TextBox\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"ComboBox\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"DatePicker\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"Calendar\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"CheckBox\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"Slider\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"Button\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"TabItem\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"DataGrid\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"DataGridColumnHeader\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"ListBox\">", xaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsEnabled\" Value=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsSelected\" Value=\"True\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_Contains_Storage_Picker_And_Persistence_Hooks()
    {
        var xaml = ReadRepoFile("src", "BossCam.Desktop", "MainWindow.xaml");
        var code = ReadRepoFile("src", "BossCam.Desktop", "MainWindow.xaml.cs");

        Assert.Contains("VideoRecordingStoragePath", xaml, StringComparison.Ordinal);
        Assert.Contains("AudioRecordingStoragePath", xaml, StringComparison.Ordinal);
        Assert.Contains("ScreenshotStoragePath", xaml, StringComparison.Ordinal);
        Assert.Contains("BrowseVideoRecordingStorage_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("BrowseAudioRecordingStorage_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("BrowseScreenshotStorage_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("FolderBrowserDialog", code, StringComparison.Ordinal);
        Assert.Contains("LoadOperatorStorageSettings()", code, StringComparison.Ordinal);
        Assert.Contains("SaveOperatorStorageSettings()", code, StringComparison.Ordinal);
        Assert.Contains("EnsureRecordingProfileConfiguredAsync()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_Apply_Path_Preserves_Operator_Dirty_State_And_Grouped_Writes()
    {
        var code = ReadRepoFile("src", "BossCam.Desktop", "MainWindow.xaml.cs");
        var typed = ReadRepoFile("src", "BossCam.Core", "Services", "TypedSettingsService.cs");

        Assert.Contains("IsOperatorWritableField(field)", code, StringComparison.Ordinal);
        Assert.Contains("BuildFieldApplyOutcomes", code, StringComparison.Ordinal);
        Assert.Contains("attemptedValues.TryGetValue(fieldKey, out var failedValue)", code, StringComparison.Ordinal);
        Assert.Contains("SetValue(fieldKey, failedValue);", code, StringComparison.Ordinal);
        Assert.Contains("TypedControlWritePolicy.Decide(field, groupedResults.GetValueOrDefault(change.FieldKey), expertOverride)", typed, StringComparison.Ordinal);
        var policy = ReadRepoFile("src", "BossCam.Core", "Services", "TypedControlWritePolicy.cs");
        Assert.Contains("ForcedFieldClassification.Writable", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void Focused_Orchestration_Ports_Are_Required_Di_Dependencies()
    {
        var recording = ReadRepoFile("src", "BossCam.Core", "Services", "RecordingService.cs");
        var typed = ReadRepoFile("src", "BossCam.Core", "Services", "TypedSettingsService.cs");

        Assert.Contains("IRecordingStore recordingStore", recording, StringComparison.Ordinal);
        Assert.Contains("RecordingProcessSupervisor processSupervisor", recording, StringComparison.Ordinal);
        Assert.DoesNotContain("IRecordingStore? recordingStore = null", recording, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordingProcessSupervisor? processSupervisor = null", recording, StringComparison.Ordinal);
        Assert.Contains("ITypedControlStore typedControlStore", typed, StringComparison.Ordinal);
        Assert.DoesNotContain("ITypedControlStore? typedControlStore = null", typed, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingService_Does_Not_Retain_Obsolete_Snapshot_Pipeline()
    {
        var recording = ReadRepoFile("src", "BossCam.Core", "Services", "RecordingService.cs");

        Assert.DoesNotContain("StartSnapshotPipeline", recording, StringComparison.Ordinal);
        Assert.DoesNotContain("private static string BashQuote", recording, StringComparison.Ordinal);
    }

    [Fact]
    public void Nvr_Code_Surfaces_Actionable_Failure_State_Instead_Of_Blank_Tiles()
    {
        var nvr = ReadRepoFile("src", "BossCam.Desktop", "MainWindow.Nvr.cs");
        var session = ReadRepoFile("src", "BossCam.Desktop", "NvrFrameDecodeSession.cs");

        Assert.Contains("No recording found for selected time.", nvr, StringComparison.Ordinal);
        Assert.Contains("Playback file missing", nvr, StringComparison.Ordinal);
        Assert.Contains("No frames received from any live source.", nvr, StringComparison.Ordinal);
        Assert.Contains("BuildNvrFailureDiagnostics", nvr, StringComparison.Ordinal);
        Assert.Contains("ffmpegArgs=", nvr, StringComparison.Ordinal);
        Assert.Contains("framesDecoded=", nvr, StringComparison.Ordinal);
        Assert.Contains("lastFrameTimestamp=", nvr, StringComparison.Ordinal);
        Assert.Contains("stderrTail=", nvr, StringComparison.Ordinal);
        Assert.Contains("No frames received during startup.", session, StringComparison.Ordinal);
        Assert.Contains("FFmpeg exited before the first frame.", session, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] segments)
        => File.ReadAllText(Path.Combine(FindRepoRoot(), Path.Combine(segments)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BossCamSuite.Linux.sln"))
                || File.Exists(Path.Combine(directory.FullName, "BossCamSuite.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate BossCamSuite solution file from the test output directory.");
    }
}
