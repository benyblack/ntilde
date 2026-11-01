using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Storage;

namespace Ntilde.Platform.TestHost;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2) return 2;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
            if (args.Length == 2 && args[1] == "--hold-lock")
            {
                using var lease = new FileStream(args[0] + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                Reply("ready");
                Console.In.ReadLine();
                return 0;
            }
            var store = new JsonSshProfileStore(args[0]);
            Reply("ready");
            while (Console.In.ReadLine() is { } line)
            {
                string[] parts = line.Split('\t', 3);
                switch (parts[0])
                {
                    case "save":
                        store.SaveProfile(new SshProfile { Id = Guid.Parse(parts[1]), Name = parts[2], Host = "test.invalid", User = "test" });
                        Reply("ok");
                        break;
                    case "delete":
                        Reply(store.DeleteProfile(Guid.Parse(parts[1])) ? "ok" : "missing");
                        break;
                    case "list":
                        Reply("ids" + string.Concat(store.GetProfiles().Select(p => "\t" + p.Id.ToString("N")).Order(StringComparer.Ordinal)));
                        break;
                    default:
                        throw new ArgumentException("Unknown profile test command.");
                }
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Reply(string text)
    {
        Console.Out.WriteLine(text);
        Console.Out.Flush();
    }
}
