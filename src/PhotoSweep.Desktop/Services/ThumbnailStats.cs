using System.Diagnostics;
using System.IO;

namespace PhotoSweep.Desktop.Services;

/// <summary>
/// Debug builds only: counts what happened to each thumbnail request, to check that fast scrolling skips the decodes
/// for rows scrolled past. Prints a line to the Debug output (Visual Studio's Output window) once a second while
/// the numbers change. Set <c>PHOTOSWEEP_THUMBNAIL_LOG</c> to a file path to also append the lines there.
/// </summary>
/// <remarks>
/// Every request ends as exactly one of: served from the cache, skipped (the row was scrolled away before its
/// thumbnail was ready: during the settle delay, while waiting for a decode slot, or part-way through decoding), or
/// decoded. "Too late" is the subset of decodes that finished after their
/// row had been recycled, so the result was dropped. The methods are [Conditional("DEBUG")]: in a Release build the
/// compiler removes every call to them.
/// </remarks>
internal static class ThumbnailStats
{
    private static int _requested, _cacheHits, _skipped, _decoded, _tooLate;

    [Conditional("DEBUG")]
    public static void Requested() => Count(ref _requested);

    [Conditional("DEBUG")]
    public static void CacheHit() => Count(ref _cacheHits);

    [Conditional("DEBUG")]
    public static void Skipped() => Count(ref _skipped);

    [Conditional("DEBUG")]
    public static void Decoded() => Count(ref _decoded);

    [Conditional("DEBUG")]
    public static void TooLate() => Count(ref _tooLate);

    private static void Count(ref int counter)
    {
        Interlocked.Increment(ref counter);
#if DEBUG
        Printer.Start();
#endif
    }

#if DEBUG
    private static class Printer
    {
        private static readonly string? LogPath = Environment.GetEnvironmentVariable("PHOTOSWEEP_THUMBNAIL_LOG");
        private static readonly Timer Timer = new(_ => Print(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        private static string _lastLine = "";

        /// <summary>Does nothing itself; the first call runs the static initializer, which starts the timer.</summary>
        public static void Start() => GC.KeepAlive(Timer);

        private static void Print()
        {
            var line = $"Thumbnails: requested {Volatile.Read(ref _requested)}, from cache {Volatile.Read(ref _cacheHits)}, "
                + $"skipped {Volatile.Read(ref _skipped)}, decoded {Volatile.Read(ref _decoded)} "
                + $"(too late for their row: {Volatile.Read(ref _tooLate)})";
            if (line == _lastLine)
                return;

            _lastLine = line;
            Debug.WriteLine(line);
            if (LogPath is null)
                return;

            try
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // A diagnostics log must never break the app.
            }
        }
    }
#endif
}
