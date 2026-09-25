using PhotoSweep.Core.Cleanup;
using PhotoSweep.Core.Grouping;

namespace PhotoSweep.Presentation.Services;

/// <summary>
/// The clean-up engine as the results page sees it: <see cref="CleanupPlan.Validate"/> and <see cref="ReviewFolder"/>.
/// <see cref="CleanupService"/> is the real one; tests use a fake that finishes each move or undo on command.
/// </summary>
public interface ICleanupService
{
    /// <summary>Checks a selection before anything moves. In-memory only, so it's synchronous.</summary>
    CleanupValidation Validate(IReadOnlyList<PhotoGroup> groups, IEnumerable<string> pathsToRemove, IReadOnlyList<string> roots);

    /// <summary>Moves the plan's files to the review folder, off the UI thread. Per-file failures are in the result, not thrown.</summary>
    Task<CleanupResult> MoveAsync(CleanupPlan plan);

    /// <summary>Puts a batch back, off the UI thread. Per-file failures are in the result, not thrown.</summary>
    Task<UndoResult> UndoAsync(CleanupBatch batch);
}
