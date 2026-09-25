using System.Windows;
using Microsoft.Win32;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Desktop.Services;

/// <summary>The Windows "Select folder" dialog (.NET 8+ <see cref="OpenFolderDialog"/>), allowing several folders at once.</summary>
public sealed class WpfFolderPicker : IFolderPicker
{
    public IReadOnlyList<string> PickFolders()
    {
        var dialog = new OpenFolderDialog { Title = "Choose folders to scan", Multiselect = true };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderNames : [];
    }
}
