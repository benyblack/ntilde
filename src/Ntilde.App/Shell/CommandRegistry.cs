using System;
using System.Collections.Generic;
using System.Linq;

namespace Ntilde.Shell
{
    public class TerminalCommand
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Category { get; set; } = "General";
        public Action Action { get; set; } = () => { };
        public string Shortcut { get; set; } = "";

        // Titles that already carry a prefix ("Theme: Dracula", "Tab: Next (MRU)") are shown as
        // they are; prefixing the category too gave "Theme: Theme: Dracula".
        public string FullTitle => Title.Contains(": ", StringComparison.Ordinal) ? Title : $"{Category}: {Title}";
    }

    public static class CommandRegistry
    {
        private static readonly List<TerminalCommand> _commands = new();

        public static void Register(string title, string category, Action action, string shortcut = "", string id = "")
        {
            _commands.Add(new TerminalCommand
            {
                Id = string.IsNullOrWhiteSpace(id) ? title : id,
                Title = title,
                Category = category,
                Action = action,
                Shortcut = shortcut
            });
        }

        public static void Clear() => _commands.Clear();

        /// <summary>
        /// A snapshot of the registered commands. It used to be the backing list itself (#112), so any caller
        /// could add or remove commands for everyone, and a later Clear and re-register (every settings save
        /// rebuilds the palette) changed the collection under whoever was holding it.
        /// </summary>
        public static IReadOnlyList<TerminalCommand> GetCommands() => _commands.ToArray();

        /// <summary>The matching commands, as a new list the caller owns - for an empty query, all of them.</summary>
        public static List<TerminalCommand> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return _commands.ToList();

            var q = query.ToLowerInvariant();
            return _commands
                .Where(c => c.Title.ToLowerInvariant().Contains(q) || c.Category.ToLowerInvariant().Contains(q))
                .OrderBy(c => c.Category)
                .ThenBy(c => c.Title)
                .ToList();
        }
    }
}
