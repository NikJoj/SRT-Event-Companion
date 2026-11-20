using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Srt.EventCompanion.Core;

public sealed record OnlineJoinTarget(IPAddress ServerAddress, int HttpPort)
{
    /// <param name="password">Filled in for the driver in Content Manager's join dialog.</param>
    public Uri ToUri(string? password = null)
    {
        if (ServerAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Assetto Corsa online join requires an IPv4 server address.", nameof(ServerAddress));
        }

        if (HttpPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(HttpPort), "Server HTTP port must be between 1 and 65535.");
        }

        var builder = new UriBuilder("acmanager", "race", -1, "/online/join")
        {
            Query = $"httpPort={HttpPort}&ip={ServerAddress}" +
                (string.IsNullOrEmpty(password) ? string.Empty : $"&plainPassword={Uri.EscapeDataString(password)}")
        };
        return builder.Uri;
    }
}

public interface IContentManagerLauncher
{
    void Launch(Uri link);
}

public sealed class ContentManagerLauncher : IContentManagerLauncher
{
    public void Launch(OnlineJoinTarget target) => Launch(target.ToUri());

    /// <summary>Hands an acmanager:// link (join dialog or direct start) to Content Manager.</summary>
    public void Launch(Uri link)
    {
        var process = Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
        if (process is null) throw new InvalidOperationException("Windows could not hand the join link to Content Manager.");
    }
}
