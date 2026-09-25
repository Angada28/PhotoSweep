using PhotoSweep.Core.Cleanup;
using PhotoSweep.Core.Grouping;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Tests.Presentation;

/// <summary>
/// Validates with the real <see cref="CleanupPlan.Validate"/> (it's pure, in-memory) unless told to report problems.
/// Moves and undos stay pending until the test finishes them, so it controls exactly when each completes.
/// </summary>
internal sealed class FakeCleanupService : ICleanupService
{
    public List<IReadOnlyList<string>> Validations { get; } = [];

    public List<MoveCall> Moves { get; } = [];

    public List<UndoCall> Undos { get; } = [];

    public MoveCall LastMove => Moves[^1];

    public UndoCall LastUndo => Undos[^1];

    /// <summary>When set, Validate refuses with these instead of checking.</summary>
    public IReadOnlyList<CleanupProblem>? Problems { get; set; }

    public CleanupValidation Validate(IReadOnlyList<PhotoGroup> groups, IEnumerable<string> pathsToRemove, IReadOnlyList<string> roots)
    {
        var paths = pathsToRemove.ToList();
        Validations.Add(paths);
        return Problems is { } problems ? new CleanupValidation(null, problems) : CleanupPlan.Validate(groups, paths, roots);
    }

    public Task<CleanupResult> MoveAsync(CleanupPlan plan)
    {
        var call = new MoveCall(plan, Moves.Count + 1);
        Moves.Add(call);
        return call.Task;
    }

    public Task<UndoResult> UndoAsync(CleanupBatch batch)
    {
        var call = new UndoCall(batch);
        Undos.Add(call);
        return call.Task;
    }
}

internal sealed class MoveCall(CleanupPlan plan, int number)
{
    private readonly TaskCompletionSource<CleanupResult> _result = new();

    public CleanupPlan Plan { get; } = plan;

    public Task<CleanupResult> Task => _result.Task;

    public IReadOnlyList<string> Paths => Plan.Groups.SelectMany(g => g.Remove).Select(m => m.File.Path).ToList();

    /// <summary>Where this batch "went": a numbered folder, so batches in one test are told apart.</summary>
    public string BatchFolder => $@"C:\Photos\_PhotoSweep Removed\batch {number}";

    /// <summary>Moves everything in the plan except the listed failures, as the real engine would report it.</summary>
    public CleanupResult Complete(params (string Path, CleanupFailureReason Reason)[] failures)
    {
        var failed = failures.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var moved = Plan.Groups.SelectMany(g => g.Remove).Where(m => !failed.Contains(m.File.Path)).ToList();
        var batch = new CleanupBatch(Guid.NewGuid(), DateTime.UtcNow, moved.Count > 0 ? [BatchFolder + @"\manifest.json"] : [],
            moved.Count, moved.Sum(m => m.File.SizeBytes));
        var result = new CleanupResult(batch,
            moved.Select(m => new MovedFile(m.File.Path, Path.Combine(BatchFolder, m.RelativePath))).ToList(),
            failures.Select(f => new CleanupFailure(f.Path, f.Reason, $"engine message for {f.Reason}")).ToList());
        _result.SetResult(result);
        return result;
    }

    public void Fail(Exception ex) => _result.SetException(ex);
}

internal sealed class UndoCall(CleanupBatch batch)
{
    private readonly TaskCompletionSource<UndoResult> _result = new();

    public CleanupBatch Batch { get; } = batch;

    public Task<UndoResult> Task => _result.Task;

    public void Complete(IEnumerable<RestoredFile> restored, params CleanupFailure[] failures) =>
        _result.SetResult(new UndoResult(restored.ToList(), failures));

    /// <summary>Every moved file comes back to where it was, except any the test renames.</summary>
    public void RestoreAll(CleanupResult moved, params (string Original, string RestoredAs)[] renamed)
    {
        var renames = renamed.ToDictionary(r => r.Original, r => r.RestoredAs, StringComparer.OrdinalIgnoreCase);
        Complete(moved.Moved.Select(m => new RestoredFile(m.OriginalPath, renames.GetValueOrDefault(m.OriginalPath, m.OriginalPath))));
    }

    public void Fail(Exception ex) => _result.SetException(ex);
}

internal sealed class FakeShellService : IShellService
{
    public List<string> Opened { get; } = [];

    public bool Succeeds { get; set; } = true;

    public bool OpenFolder(string path)
    {
        Opened.Add(path);
        return Succeeds;
    }
}
