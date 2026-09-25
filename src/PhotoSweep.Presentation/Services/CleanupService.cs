using PhotoSweep.Core.Cleanup;
using PhotoSweep.Core.Grouping;

namespace PhotoSweep.Presentation.Services;

/// <summary>
/// The real engine. Moves and undos run on the thread pool: each file is a rename plus a manifest rewrite, which is
/// quick per file but adds up over a large batch, and must not freeze the window.
/// </summary>
/// <remarks>
/// Lives in Presentation, not Desktop, for the same reason as <see cref="ScanService"/>: nothing here is Windows- or
/// WPF-specific, so tests can run the real thing against a temp folder.
/// </remarks>
public sealed class CleanupService(ReviewFolder? reviewFolder = null) : ICleanupService
{
    private readonly ReviewFolder _review = reviewFolder ?? new ReviewFolder();

    public CleanupValidation Validate(IReadOnlyList<PhotoGroup> groups, IEnumerable<string> pathsToRemove, IReadOnlyList<string> roots) =>
        CleanupPlan.Validate(groups, pathsToRemove, roots);

    public Task<CleanupResult> MoveAsync(CleanupPlan plan) => Task.Run(() => _review.MoveToReview(plan));

    public Task<UndoResult> UndoAsync(CleanupBatch batch) => Task.Run(() => _review.Undo(batch));
}
