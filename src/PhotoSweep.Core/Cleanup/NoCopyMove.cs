using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PhotoSweep.Core.Cleanup;

/// <summary>
/// Moves a file by renaming it, and fails rather than copying it. A rename only changes directory entries: the file's
/// data is never opened or read, so an online-only OneDrive file is moved without being downloaded.
/// </summary>
/// <remarks>
/// <see cref="File.Move(string, string)"/> isn't enough: on Windows it passes <c>MOVEFILE_COPY_ALLOWED</c>, so when the
/// destination is on another volume (e.g. a mount point inside the scanned folder) it silently copies the file instead,
/// which reads every byte. Calling <c>MoveFileExW</c> with no flags makes that case fail with
/// <c>ERROR_NOT_SAME_DEVICE</c>, and never overwrites an existing destination.
/// </remarks>
internal static partial class NoCopyMove
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;

    /// <exception cref="IOException">The move failed; use <see cref="IsAlreadyExists"/> to spot a taken destination.</exception>
    /// <exception cref="UnauthorizedAccessException">Access denied.</exception>
    /// <exception cref="PlatformNotSupportedException">Not running on Windows.</exception>
    public static void Move(string source, string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "PhotoSweep moves files with the Windows MoveFileExW API so they are never copied; other platforms aren't supported.");
        }

        if (MoveFileExW(source, destination, flags: 0))
            return;

        var error = Marshal.GetLastPInvokeError();
        var message = $"{new Win32Exception(error).Message} ({source} → {destination})";
        if (error == ErrorAccessDenied)
            throw new UnauthorizedAccessException(message);
        throw new IOException(message, HResultFromWin32(error));
    }

    public static bool IsAlreadyExists(IOException e) =>
        e.HResult == HResultFromWin32(ErrorFileExists) || e.HResult == HResultFromWin32(ErrorAlreadyExists);

    // HRESULT_FROM_WIN32: the same mapping .NET uses for its own IOExceptions.
    private static int HResultFromWin32(int error) => unchecked((int)0x80070000 | error);

    // Source-generated: the marshalling stub is emitted at compile time instead of being built by reflection at run time.
    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileExW(string existingFileName, string newFileName, uint flags);
}
