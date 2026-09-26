using System.Globalization;

namespace PhotoSweep.Eval;

/// <summary>Thrown for a bad command line; Program prints the message and the usage text.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>
/// <c>command positional… --name value…</c>. Hand-rolled rather than System.CommandLine: three commands with a few
/// options don't justify a package, and every option takes a value, so there's nothing ambiguous to parse.
/// </summary>
public sealed class CommandLine
{
    private readonly List<string> _positional = [];
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; private init; } = "";

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            throw new UsageException("No command given.");

        var result = new CommandLine { Command = args[0].ToLowerInvariant() };
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Count)
                    throw new UsageException($"{args[i]} needs a value.");

                result._options[args[i][2..]] = args[++i];
            }
            else
            {
                result._positional.Add(args[i]);
            }
        }

        return result;
    }

    public string Positional(int index, string name) =>
        index < _positional.Count ? _positional[index] : throw new UsageException($"Missing <{name}>.");

    public string? Option(string name) => _options.GetValueOrDefault(name);

    public string RequiredOption(string name) => Option(name) ?? throw new UsageException($"--{name} is required.");

    public int Int(string name, int defaultValue)
    {
        if (Option(name) is not { } text)
            return defaultValue;

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new UsageException($"--{name} must be a whole number, got \"{text}\".");
    }
}
