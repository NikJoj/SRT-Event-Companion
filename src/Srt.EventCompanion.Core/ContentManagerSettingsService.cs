using System.IO.Compression;
using System.Text;

namespace Srt.EventCompanion.Core;

public sealed record ContentManagerPlayerNameChange(string? CurrentPlayerName, string ProposedPlayerName);

/// <summary>
/// Reads and changes the driver name in Content Manager's settings file (Values.data).
/// </summary>
/// <remarks>
/// The format follows Content Manager's own Storage class (github.com/gro-ove/actools, FirstFloor.ModernUI/Helpers/Storage.cs):
/// the text "version: 2", then one "key\tvalue" line per setting, with \\, \n and \t escaped. CM normally saves it
/// LZF-compressed behind a 0x0B flag byte (0x00 = deflate; plain text is also accepted). The file must be decompressed,
/// edited as text and written back as a valid file: patching bytes inside the compressed data corrupts it, and CM then
/// silently loads its own backup copy, so the name never changes.
/// </remarks>
public sealed class ContentManagerSettingsService
{
    public const string PlayerNameKey = "Settings.DriveSettings.PlayerName";
    // Used for online servers when "different name online" is on; set it too so the server always sees the driver.
    public const string PlayerNameOnlineKey = "Settings.DriveSettings.PlayerNameOnline";

    private const byte DeflateFlag = 0;
    private const byte LzfFlag = 11;

    public string DefaultValuesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AcTools Content Manager",
        "Values.data");

    public ContentManagerPlayerNameChange PreviewPlayerNameChange(string valuesPath, string playerName)
    {
        ValidatePlayerName(playerName);
        return new ContentManagerPlayerNameChange(ReadPlayerName(valuesPath), playerName);
    }

    public string ApplyPlayerNameChange(string valuesPath, string playerName, DateTimeOffset now)
    {
        ValidatePlayerName(playerName);
        var lines = ReadLines(valuesPath);
        SetValue(lines, PlayerNameKey, playerName);
        SetValue(lines, PlayerNameOnlineKey, playerName);
        var updated = EncodeFile(lines);

        var backupPath = $"{valuesPath}.{now:yyyyMMddHHmmss}.bak";
        var temporaryPath = $"{valuesPath}.{Guid.NewGuid():N}.tmp";
        File.Copy(valuesPath, backupPath, overwrite: false);
        try
        {
            File.WriteAllBytes(temporaryPath, updated);
            File.Replace(temporaryPath, valuesPath, destinationBackupFileName: null);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return backupPath;
    }

    public string? ReadPlayerName(string valuesPath) => ReadValue(valuesPath, PlayerNameKey);

    public string? ReadValue(string valuesPath, string key)
    {
        foreach (var line in ReadLines(valuesPath))
        {
            var tab = line.IndexOf('\t');
            if (tab > 0 && Decode(line[..tab]) == key) return Decode(line[(tab + 1)..]);
        }
        return null;
    }

    // ── File format ──────────────────────────────────────────────────────────

    private static List<string> ReadLines(string valuesPath)
    {
        var bytes = File.ReadAllBytes(valuesPath);
        var text = Encoding.UTF8.GetString(DecodeBytes(bytes));
        var lines = text.Split('\n').ToList();
        if (lines.Count == 0 || !lines[0].StartsWith("version:", StringComparison.Ordinal))
            throw new InvalidOperationException("Content Manager's settings file (Values.data) is in a format this app does not recognise. Contact SRT Labs.");
        return lines;
    }

    private static byte[] DecodeBytes(byte[] bytes)
    {
        if (bytes.Length == 0) throw new InvalidOperationException("Content Manager's settings file (Values.data) is empty. Open Content Manager once, then try again.");
        if (bytes[0] == LzfFlag) return LzfDecompress(bytes, 1);
        var deflate = bytes[0] == DeflateFlag;
        if (!deflate && !bytes.Any(b => b < 0x20 && b != '\t' && b != '\n' && b != '\r')) return bytes; // plain text

        using var input = new MemoryStream(bytes, deflate ? 1 : 0, bytes.Length - (deflate ? 1 : 0));
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflater.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Writes the file the way CM does: LZF flag, then LZF data (as literal runs, which any LZF reader accepts).</summary>
    private static byte[] EncodeFile(List<string> lines)
    {
        var raw = Encoding.UTF8.GetBytes(string.Join('\n', lines));
        using var output = new MemoryStream(raw.Length + raw.Length / 32 + 2);
        output.WriteByte(LzfFlag);
        for (var offset = 0; offset < raw.Length; offset += 32)
        {
            var count = Math.Min(32, raw.Length - offset);
            output.WriteByte((byte)(count - 1));
            output.Write(raw, offset, count);
        }
        return output.ToArray();
    }

    /// <summary>Standard LibLZF decompression (as in CM's Lzf.Decompress).</summary>
    internal static byte[] LzfDecompress(byte[] input, int offset)
    {
        var result = new List<byte>(input.Length * 3);
        var i = offset;
        while (i < input.Length)
        {
            int ctrl = input[i++];
            if (ctrl < 32)
            {
                var run = ctrl + 1;
                if (i + run > input.Length) throw new InvalidOperationException("Content Manager's settings file (Values.data) is damaged.");
                for (var k = 0; k < run; k++) result.Add(input[i++]);
            }
            else
            {
                var length = ctrl >> 5;
                var reference = result.Count - ((ctrl & 0x1f) << 8) - 1;
                if (length == 7) length += input[i++];
                reference -= input[i++];
                if (reference < 0) throw new InvalidOperationException("Content Manager's settings file (Values.data) is damaged.");
                for (var k = 0; k < length + 2; k++) result.Add(result[reference++]);
            }
        }
        return result.ToArray();
    }

    private static void SetValue(List<string> lines, string key, string value)
    {
        var encoded = Encode(key) + "\t" + Encode(value);
        for (var index = 1; index < lines.Count; index++)
        {
            var tab = lines[index].IndexOf('\t');
            if (tab > 0 && Decode(lines[index][..tab]) == key)
            {
                lines[index] = encoded;
                return;
            }
        }
        lines.Add(encoded);
    }

    private static string Encode(string s) => s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "").Replace("\b", "");

    private static string Decode(string s)
    {
        var result = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\') { result.Append(s[i]); continue; }
            if (++i >= s.Length) break;
            result.Append(s[i] switch { 'n' => '\n', 't' => '\t', var c => c });
        }
        return result.ToString();
    }

    private static void ValidatePlayerName(string playerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);
        if (playerName.Length > 64) throw new ArgumentException("Player name must be 64 characters or fewer.", nameof(playerName));
        if (playerName.Contains('\r') || playerName.Contains('\n') || playerName.Contains('\t'))
        {
            throw new ArgumentException("Player name cannot include a line break or tab.", nameof(playerName));
        }
    }
}
