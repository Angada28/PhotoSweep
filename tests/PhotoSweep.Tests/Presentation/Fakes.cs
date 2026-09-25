using PhotoSweep.Core.Scanning;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Tests.Presentation;

/// <summary>Returns queued answers in order, as if the user picked those folders; an empty queue acts like Cancel.</summary>
internal sealed class FakeFolderPicker : IFolderPicker
{
    private readonly Queue<IReadOnlyList<string>> _answers = new();

    public int Calls { get; private set; }

    public FakeFolderPicker WillPick(params string[] folders)
    {
        _answers.Enqueue(folders);
        return this;
    }

    public IReadOnlyList<string> PickFolders()
    {
        Calls++;
        return _answers.TryDequeue(out var folders) ? folders : [];
    }
}

internal sealed class FakeKnownFolders(string? pictures = null, string? oneDrive = null) : IKnownFolders
{
    public string? Pictures { get; } = pictures;

    public string? OneDrive { get; } = oneDrive;
}

/// <summary>Every file is on this PC unless the test says otherwise. Counts checks, to prove when they happen.</summary>
internal sealed class FakeFileAvailability : IFileAvailability
{
    private readonly Dictionary<string, FileAvailability> _states = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Checked { get; } = [];

    public FakeFileAvailability Set(string path, FileAvailability state)
    {
        _states[path] = state;
        return this;
    }

    public FileAvailability Check(string path)
    {
        Checked.Add(path);
        return _states.GetValueOrDefault(path, FileAvailability.Local);
    }
}
