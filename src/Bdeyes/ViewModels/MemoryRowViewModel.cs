using System.Text;
using Bdeyes.Models;

namespace Bdeyes.ViewModels;

public sealed class MemoryRowViewModel
{
    private const int PreviewLength = 220;

    public MemoryRowViewModel(BdMemoryEntry memory)
    {
        Memory = memory;
        Preview = BuildPreview(memory.Value);
    }

    public BdMemoryEntry Memory { get; }

    public string Key => Memory.Key;

    public string Value => Memory.Value;

    public string Preview { get; }

    public string AutomationName => $"Memory {Key}. {Preview}";

    public bool Matches(string query) =>
        Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Value.Contains(query, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => AutomationName;

    private static string BuildPreview(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Empty memory";
        }

        var text = value.AsSpan().Trim();
        var builder = new StringBuilder(Math.Min(text.Length, PreviewLength));
        var pendingSpace = false;
        var truncated = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                if (builder.Length >= PreviewLength)
                {
                    truncated = true;
                    break;
                }

                builder.Append(' ');
                pendingSpace = false;
            }

            if (builder.Length >= PreviewLength)
            {
                truncated = true;
                break;
            }

            builder.Append(character);
        }

        if (truncated)
        {
            builder[^1] = '…';
        }

        return builder.ToString();
    }
}
