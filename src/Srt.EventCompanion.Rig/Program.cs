using Srt.EventCompanion.Core;
using System.Net;

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

if (string.Equals(args[0], "inspect", StringComparison.OrdinalIgnoreCase))
{
    var paths = AssettoCorsaPaths.Detect(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    if (paths is null)
    {
        Console.Error.WriteLine("Assetto Corsa installation or active race.ini profile was not found.");
        return 1;
    }

    var profile = new RigProfileService().Read(paths.RaceIniPath);
    Console.WriteLine("Assetto Corsa rig profile found.");
    Console.WriteLine($"Install directory: {paths.InstallDirectory}");
    Console.WriteLine($"Profile path: {paths.RaceIniPath}");
    Console.WriteLine($"Offline driver name: {profile.OfflineDriverName ?? "<unset>"}");
    Console.WriteLine($"Multiplayer driver name: {profile.MultiplayerDriverName ?? "<unset>"}");
    return 0;
}

if (string.Equals(args[0], "join", StringComparison.OrdinalIgnoreCase))
{
    var ipAddress = ReadOption(args, "--ip");
    var httpPort = ReadOption(args, "--http-port");
    if (!IPAddress.TryParse(ipAddress, out var address) || !int.TryParse(httpPort, out var port))
    {
        Console.Error.WriteLine("A valid --ip and --http-port are required.");
        return 2;
    }

    var target = new OnlineJoinTarget(address, port);
    var joinUri = target.ToUri();
    Console.WriteLine($"Content Manager join URI: {joinUri}");
    if (!args.Contains("--launch", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine("Dry run only. Add --launch to open this URI in Content Manager.");
        return 0;
    }

    new ContentManagerLauncher().Launch(target);
    Console.WriteLine("Content Manager launch requested.");
    return 0;
}

PrintUsage();
return 2;

static string? ReadOption(string[] arguments, string option)
{
    var index = Array.FindIndex(arguments, argument => string.Equals(argument, option, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  srt-rig inspect");
    Console.Error.WriteLine("  srt-rig join --ip <IPv4> --http-port <port> [--launch]");
    Console.Error.WriteLine("Passwords are never accepted on the command line.");
}
