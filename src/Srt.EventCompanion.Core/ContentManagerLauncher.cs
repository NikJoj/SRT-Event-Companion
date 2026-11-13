using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Srt.EventCompanion.Core;

public sealed record OnlineJoinTarget(IPAddress ServerAddress, int HttpPort)
{
    public Uri ToUri()
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
            Query = $"httpPort={HttpPort}&ip={ServerAddress}"
        };
        return builder.Uri;
    }
}

public interface IContentManagerLauncher
{
    void Launch(OnlineJoinTarget target);
}

public sealed class ContentManagerLauncher : IContentManagerLauncher
{
    public void Launch(OnlineJoinTarget target)
    {
        var process = Process.Start(new ProcessStartInfo(target.ToUri().AbsoluteUri) { UseShellExecute = true });
        if (process is null) throw new InvalidOperationException("Windows could not hand the join link to Content Manager.");
    }
}
