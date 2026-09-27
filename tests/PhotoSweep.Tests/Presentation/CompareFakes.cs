using PhotoSweep.Core.Imaging;
using PhotoSweep.Presentation;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Tests.Presentation;

/// <summary>Records the compare view-models shown, and plays the window's part: closes when asked.</summary>
internal sealed class FakeWindowService : IWindowService
{
    public List<CompareViewModel> Shown { get; } = [];

    /// <summary>How many times a view-model asked its window to close.</summary>
    public int CloseRequests { get; private set; }

    public CompareViewModel Last => Shown[^1];

    public void ShowCompare(CompareViewModel viewModel)
    {
        if (!Shown.Contains(viewModel))
            viewModel.CloseRequested += (_, _) => CloseRequests++;
        Shown.Add(viewModel);
    }
}

/// <summary>
/// Records every preview request. By default each completes at once with a fake image (a string naming the file and
/// size); with <see cref="Hold"/> they stay pending until the test completes them, so it can check cancellation and
/// late results. Tokens are only recorded, never acted on, so a test can complete a cancelled load and prove it's dropped.
/// </summary>
internal sealed class FakePreviewLoader : IPreviewLoader
{
    public List<PreviewCall> Calls { get; } = [];

    public bool Hold { get; set; }

    /// <summary>Photos smaller than any pane: their preview is the whole photo.</summary>
    public HashSet<string> Small { get; } = [];

    public IEnumerable<string> Paths => Calls.Select(c => c.Path);

    public Task<PreviewImage> LoadAsync(string path, DateTime lastWriteUtc, PreviewSize size, CancellationToken ct)
    {
        var call = new PreviewCall(path, size, ct, Small.Contains(path));
        Calls.Add(call);
        if (!Hold)
            call.CompleteOk();
        return call.Task;
    }

    public IReadOnlyList<PreviewCall> For(string path) => Calls.Where(c => c.Path == path).ToList();
}

internal sealed class PreviewCall(string path, PreviewSize size, CancellationToken token, bool small = false)
{
    private readonly TaskCompletionSource<PreviewImage> _result = new();

    public string Path { get; } = path;

    public PreviewSize Size { get; } = size;

    public CancellationToken Token { get; } = token;

    public Task<PreviewImage> Task => _result.Task;

    /// <summary>What the pane shows for this load: unique per call, so a test can tell which load's result is on screen.</summary>
    public string Image => $"{Path} @ {Size}";

    /// <summary>A picture that fills the box (i.e. was scaled down), unless it's a small photo or asked for actual size.</summary>
    public void CompleteOk() => _result.SetResult(
        small ? new PreviewImage(ThumbnailStatus.Ok, Image, 300, 200)
        : new PreviewImage(ThumbnailStatus.Ok, Image, Size.IsActualSize ? 4000 : Size.Width, Size.IsActualSize ? 3000 : Size.Height));

    public void Complete(ThumbnailStatus status) => _result.SetResult(new PreviewImage(status));
}
