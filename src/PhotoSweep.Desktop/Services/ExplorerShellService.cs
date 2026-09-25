using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Desktop.Services;

/// <summary>Opens folders in File Explorer.</summary>
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
}
