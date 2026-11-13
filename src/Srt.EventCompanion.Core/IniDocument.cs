namespace Srt.EventCompanion.Core;

internal sealed class IniDocument
{
    private readonly List<string> _lines;

    private IniDocument(IEnumerable<string> lines) => _lines = lines.ToList();

    public static IniDocument Parse(string content) => new(content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));

    public string? GetValue(string section, string key)
    {
        var sectionStart = FindSection(section);
        if (sectionStart < 0) return null;

        for (var index = sectionStart + 1; index < _lines.Count && !IsSection(_lines[index]); index++)
        {
            if (TryGetKeyValue(_lines[index], out var lineKey, out var value) && string.Equals(lineKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    public void SetValue(string section, string key, string value)
    {
        var sectionStart = FindSection(section);
        if (sectionStart < 0)
        {
            if (_lines.Count > 0 && !string.IsNullOrEmpty(_lines[^1])) _lines.Add(string.Empty);
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return;
        }

        for (var index = sectionStart + 1; index < _lines.Count && !IsSection(_lines[index]); index++)
        {
            if (TryGetKeyValue(_lines[index], out var lineKey, out _) && string.Equals(lineKey, key, StringComparison.OrdinalIgnoreCase))
            {
                _lines[index] = $"{key}={value}";
                return;
            }
        }

        var insertionIndex = sectionStart + 1;
        while (insertionIndex < _lines.Count && !IsSection(_lines[insertionIndex])) insertionIndex++;
        _lines.Insert(insertionIndex, $"{key}={value}");
    }

    public override string ToString() => string.Join(Environment.NewLine, _lines);

    private int FindSection(string section) => _lines.FindIndex(line => string.Equals(line.Trim(), $"[{section}]", StringComparison.OrdinalIgnoreCase));

    private static bool IsSection(string line) => line.Trim().StartsWith("[", StringComparison.Ordinal) && line.Trim().EndsWith("]", StringComparison.Ordinal);

    private static bool TryGetKeyValue(string line, out string key, out string value)
    {
        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        key = line[..separator].Trim();
        value = line[(separator + 1)..].Trim();
        return true;
    }
}
