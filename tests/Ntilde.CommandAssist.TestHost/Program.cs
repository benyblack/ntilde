using System.Globalization;
using Ntilde.CommandAssist.Models;
using Ntilde.CommandAssist.Storage;

namespace Ntilde.CommandAssist.TestHost;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length is < 1 or > 2) return 2;
        try
        {
            if (args.Length == 2 && args[1] == "--hold-lock")
            {
                using var held = new FileStream(args[0] + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                await ReplyAsync("ready");
                await Console.In.ReadLineAsync();
                return 0;
            }

            if (args.Length == 2 && args[1] == "--snippets") return await RunSnippetsAsync(args[0]);

            var store = new JsonlHistoryStore(args[0], 50);
            await ReplyAsync("ready");
            while (await Console.In.ReadLineAsync() is { } line)
            {
                string[] parts = line.Split('\t', 4);
                switch (parts[0])
                {
                    case "append":
                        await store.AppendAsync(new CommandHistoryEntry(
                            parts[1], parts[3], DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[2], CultureInfo.InvariantCulture)),
                            "bash", null, null, null, null, null, false, false, CommandCaptureSource.ShellIntegration, null));
                        await ReplyAsync("ok");
                        break;
                    case "clear":
                        await store.ClearAsync();
                        await ReplyAsync("ok");
                        break;
                    case "cap":
                        store.SetMaxEntries(int.Parse(parts[1], CultureInfo.InvariantCulture));
                        await ReplyIdsAsync(store);
                        break;
                    case "recent":
                        await ReplyIdsAsync(store);
                        break;
                    default:
                        throw new ArgumentException("Unknown history test command.");
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.ToString());
            return 1;
        }
    }

    private static async Task<int> RunSnippetsAsync(string path)
    {
        var store = new JsonSnippetStore(path);
        await ReplyAsync("ready");
        while (await Console.In.ReadLineAsync() is { } line)
        {
            string[] parts = line.Split('\t', 2);
            switch (parts[0])
            {
                case "upsert":
                    await store.UpsertAsync(new CommandSnippet(parts[1], parts[1], "echo " + parts[1], null, null, null, true, DateTimeOffset.UnixEpoch, null));
                    await ReplyAsync("ok");
                    break;
                case "remove":
                    await store.RemoveAsync(parts[1]);
                    await ReplyAsync("ok");
                    break;
                case "recent":
                    var snippets = await store.GetAllAsync();
                    await ReplyAsync("ids" + string.Concat(snippets.Select(s => s.Id).Order(StringComparer.Ordinal).Select(id => "\t" + id)));
                    break;
                default:
                    throw new ArgumentException("Unknown snippet test command.");
            }
        }
        return 0;
    }

    private static async Task ReplyIdsAsync(JsonlHistoryStore store)
    {
        var entries = await store.GetRecentAsync(1000);
        await ReplyAsync("ids" + string.Concat(entries.Select(entry => "\t" + entry.Id)));
    }

    private static async Task ReplyAsync(string response)
    {
        await Console.Out.WriteLineAsync(response);
        await Console.Out.FlushAsync();
    }
}
