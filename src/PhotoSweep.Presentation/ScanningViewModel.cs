using CommunityToolkit.Mvvm.Input;

namespace PhotoSweep.Presentation;

/// <summary>Placeholder for the scanning page, built properly in Phase 8. Shows what will be scanned and can go back.</summary>
public sealed partial class ScanningViewModel(ScanRequest request, Action goBack)
{
    public ScanRequest Request { get; } = request;

    public IReadOnlyList<string> Folders => Request.Options.Folders;

    public string LevelTitle => StrictnessOption.For(Request.Level).Title;

    [RelayCommand]
    private void Back() => goBack();
}
