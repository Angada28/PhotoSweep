using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Desktop.Services;

/// <summary>Opens folders, and shows files, in File Explorer.</summary>
public sealed class ExplorerShellService : IShellService
{
    public bool OpenFolder(string path)
    {
        if (!Directory.Exists(path))
            return false;

        try
        {
            // ArgumentList quotes the path for us, so spaces (e.g. "_PhotoSweep Removed") can't split it.
            using var process = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path } });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public bool ShowInFolder(string filePath)
    {
        // File.Exists only reads attributes, so an online-only file isn't downloaded; nor is it by /select.
        if (!File.Exists(filePath))
            return false;

        try
        {
            // Explorer wants the path quoted after the comma (/select,"C:\a b\c.jpg"), which ArgumentList can't
            // produce. Windows paths can't contain quotes, so building the string by hand is safe.
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\""));
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
