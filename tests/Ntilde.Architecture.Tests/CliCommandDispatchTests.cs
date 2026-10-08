using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Ntilde.Architecture.Tests;

/// <summary>
/// Task 7 fix round 1, finding 1: <c>Ntilde.App/Program.cs</c> is a second CLI dispatch
/// table, separate from the dev-only <c>Ntilde.Cli/Program.cs</c> shim — and it is the one
/// that matters in a shipped self-contained/AOT build, which has no <c>Ntilde.Cli</c>
/// sibling and so must serve every CLI verb itself (see the comment on <c>ReplayCommand</c>'s
/// dispatch in <c>App/Program.cs</c>). <c>BackupCommand</c> shipped wired only into the Cli shim:
/// correct-looking, tested, and unreachable in the build shape the feature actually has to work
/// in — a bare <c>backup export …</c> would fall through every check and launch the GUI.
///
/// This guards against the same mistake recurring for the next CLI command. It cross-checks two
/// "dispatch tables": what CLI-command-shaped types exist (found by reflection over the App
/// assembly for the static <c>IsSupportedCliMode(string[])</c> / <c>Execute(string[], TextWriter,
/// TextWriter, ...)</c> shape every command — <c>SshAskPassCommand</c>, <c>VtReportCommand</c>,
/// <c>ReplayCommand</c>, <c>BackupCommand</c> — already follows) against what is actually
/// dispatched from <c>App/Program.cs</c>'s <c>Main</c>.
///
/// The "is it dispatched" half is a source-text scan rather than more reflection, and that is a
/// deliberate choice, not a shortcut: reflection (or NetArchTest, which is IL/metadata-level too)
/// can enumerate a type's members, but "does <c>Main</c> call this static method" is a question
/// about call sites inside one method body, which needs either IL-instruction inspection of
/// <c>Main</c> (fragile — the dispatcher is a plain if-chain, not a data structure anything can
/// enumerate) or reading the source. A loose <c>"TypeName.IsSupportedCliMode("</c> substring
/// match is enough to catch the actual failure mode (the type is nowhere in the file) without the
/// cost of a full Roslyn parse; a false negative would need a decoy string that names a real
/// command type while dispatching nothing, which is not a realistic accident.
/// </summary>
public class CliCommandDispatchTests
{
    // The App assembly is named "Ntilde" (see LayeringTests' CommandAssist comment and
    // Ntilde.App.csproj's <AssemblyName>).
    private static Assembly App => Assembly.Load("Ntilde");

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ntilde.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    private const BindingFlags CommandMemberFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

    // Hoisted out of HasCliCommandShape (called once per type in the assembly) to satisfy
    // CA1861 — this project builds with TreatWarningsAsErrors.
    private static readonly Type[] StringArrayParameter = [typeof(string[])];

    /// <summary>
    /// A type has the CLI-command shape when it exposes both a static
    /// <c>bool IsSupportedCliMode(string[])</c> and a static <c>Execute</c> method whose first
    /// three parameters are exactly <c>(string[], TextWriter, TextWriter)</c> — the pattern every
    /// existing command follows. Trailing parameters (e.g. <c>BackupCommand</c>'s
    /// <c>rootOverride</c> test seam) are allowed only if optional, since every dispatch site
    /// calls the bare three-argument form.
    /// </summary>
    private static Type[] DiscoverCliCommandTypes(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types in a large Avalonia app assembly can fail to load in a reflection-only
            // enumeration context; the ones that did load are still meaningful to scan.
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }

        return types.Where(HasCliCommandShape).ToArray();
    }

    private static bool HasCliCommandShape(Type type)
    {
        var isSupported = type.GetMethod("IsSupportedCliMode", CommandMemberFlags, StringArrayParameter);
        if (isSupported is null || isSupported.ReturnType != typeof(bool)) return false;

        return type.GetMethods(CommandMemberFlags).Any(m => m.Name == "Execute" && HasCliExecuteSignature(m));
    }

    private static bool HasCliExecuteSignature(MethodInfo method)
    {
        var parameters = method.GetParameters();
        return parameters.Length >= 3
            && parameters[0].ParameterType == typeof(string[])
            && parameters[1].ParameterType == typeof(TextWriter)
            && parameters[2].ParameterType == typeof(TextWriter)
            && parameters.Skip(3).All(p => p.IsOptional);
    }

    [Fact]
    public void Every_CLI_command_type_is_dispatched_from_the_App_entry_point()
    {
        var commandTypes = DiscoverCliCommandTypes(App);

        // Pins that discovery itself still finds the known commands. Without this, a change to
        // the shared shape (e.g. every command renamed off Execute/IsSupportedCliMode) would
        // silently shrink "table A" to nothing and this test would pass vacuously — asserting
        // zero offenders among zero discovered commands proves nothing.
        Assert.Contains(commandTypes, t => t.Name == "BackupCommand");
        Assert.Contains(commandTypes, t => t.Name == "ReplayCommand");
        Assert.Contains(commandTypes, t => t.Name == "SshAskPassCommand");
        Assert.Contains(commandTypes, t => t.Name == "VtReportCommand");
        Assert.Contains(commandTypes, t => t.Name == "MuxCommand");

        string programSource = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ntilde.App/Program.cs"));

        var undispatched = commandTypes
            .Where(t => !programSource.Contains(t.Name + ".IsSupportedCliMode(", StringComparison.Ordinal))
            .Select(t => t.FullName ?? t.Name)
            .ToArray();

        Assert.True(undispatched.Length == 0,
            "These CLI-command-shaped types exist but are not dispatched from " +
            "src/Ntilde.App/Program.cs \u2014 the entry point a shipped self-contained/AOT build " +
            "actually runs (Ntilde.Cli/Program.cs is a dev-only shim absent from that " +
            "bundle, so wiring a command into it alone leaves the command unreachable there). Add " +
            "an IsSupportedCliMode/Execute branch to App/Program.cs's Main, following the " +
            $"ReplayCommand precedent. Offenders: {string.Join(", ", undispatched)}");
    }

    private static readonly string[] MuxAttachArgs = ["mux", "attach", "abcd"]; // CA1861
    private static readonly string[] MuxProbeConsoleArgs = ["mux", "probe-console"];
    private static readonly string[] MuxLsArgs = ["mux", "ls"];

    /// <summary>Phase 3: `mux attach` is interactive - both entry points must give it a real console (spec §6.7).</summary>
    [Fact]
    public void Mux_attach_gets_an_interactive_console_from_the_App_entry_point()
    {
        Type mux = App.GetType("Ntilde.Shell.Mux.MuxCommand", throwOnError: true)!;
        MethodInfo? needsConsole = mux.GetMethod("NeedsInteractiveConsole", CommandMemberFlags, StringArrayParameter);
        Assert.NotNull(needsConsole);
        bool NeedsConsole(string[] args) => (bool)needsConsole.Invoke(null, [args])!;
        Assert.True(NeedsConsole(MuxAttachArgs));
        Assert.True(NeedsConsole(MuxProbeConsoleArgs));
        Assert.False(NeedsConsole(MuxLsArgs));

        // Main is an if-chain that cannot be invoked without running the CLI, so this reads its
        // compiled call sequence rather than its source text: a call compiles to the same IL however
        // it is spelled (qualified, aliased, reformatted), and a comment or string naming it compiles
        // to nothing. Every PrepareInteractive call must sit under the helper: the nearest MuxCommand
        // call before it, which is the branch condition, is NeedsInteractiveConsole.
        MethodInfo main = App.GetType("Ntilde.Program", throwOnError: true)!
            .GetMethod("Main", CommandMemberFlags, StringArrayParameter)!;
        List<MethodBase> calls = CalledMethods(main);
        int[] prepareSites = Enumerable.Range(0, calls.Count)
            .Where(i => calls[i].Name == "PrepareInteractive" && calls[i].DeclaringType?.Name == "CliConsoleBindings")
            .ToArray();
        Assert.NotEmpty(prepareSites);
        foreach (int site in prepareSites)
        {
            MethodBase? guard = calls.Take(site).LastOrDefault(m => m.DeclaringType == mux);
            Assert.True(guard?.Name == "NeedsInteractiveConsole",
                "App Program.Main must call CliConsoleBindings.PrepareInteractive only under " +
                $"MuxCommand.NeedsInteractiveConsole(args); the guarding MuxCommand call is {guard?.Name ?? "<none>"}.");
        }

        string cli = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ntilde.Cli/Program.cs"));
        Assert.Contains("MuxCommand.IsSupportedCliMode(", cli, StringComparison.Ordinal);
    }

    /// <summary>
    /// Phase 4 spec §11.3: ntilde.com waits for the exit code of whatever it started, unless the GUI releases
    /// it. A release before any CLI check would hand a CLI verb's prompt back while the verb still ran, so
    /// every <c>LauncherRelease.Signal</c> in <c>Main</c> comes after every <c>IsSupportedCliMode</c> check and after
    /// the mux branch's <c>Execute</c> - a Signal inside that branch would come after every check and still
    /// release a mux verb. A mux verb keeps its launcher waiting but must not pass the event on to the
    /// daemon it may start, so <c>LauncherRelease.Discard</c> sits inside the mux branch: after its check,
    /// before its <c>Execute</c>. Compiled order, read from the IL, as in the test above.
    /// </summary>
    [Fact]
    public void The_launcher_is_released_only_on_the_GUI_path()
    {
        MethodInfo main = App.GetType("Ntilde.Program", throwOnError: true)!
            .GetMethod("Main", CommandMemberFlags, StringArrayParameter)!;
        List<MethodBase> calls = CalledMethods(main);
        int[] Sites(string type, string method) => Enumerable.Range(0, calls.Count)
            .Where(i => calls[i].Name == method && (type == "*" || calls[i].DeclaringType?.Name == type))
            .ToArray();

        int[] checks = Sites("*", "IsSupportedCliMode");
        int[] signals = Sites("LauncherRelease", "Signal");
        int muxCheck = Assert.Single(Sites("MuxCommand", "IsSupportedCliMode"));
        int muxExecute = Assert.Single(Sites("MuxCommand", "Execute"));
        int discard = Assert.Single(Sites("LauncherRelease", "Discard"));
        Assert.True(checks.Length >= 5, $"Main should check the five CLI modes; found {checks.Length} IsSupportedCliMode calls.");
        Assert.NotEmpty(signals);
        Assert.All(signals, s => Assert.True(s > checks.Max(),
            "App Program.Main calls LauncherRelease.Signal before an IsSupportedCliMode check: a CLI verb run through ntilde.com would get its prompt back while it ran."));
        Assert.All(signals, s => Assert.True(s > muxExecute,
            "App Program.Main calls LauncherRelease.Signal before MuxCommand.Execute, inside the mux branch: a mux verb run through ntilde.com would get its prompt back while it ran."));

        Assert.True(muxCheck < discard && discard < muxExecute,
            "App Program.Main must call LauncherRelease.Discard inside the mux branch, between MuxCommand.IsSupportedCliMode and MuxCommand.Execute.");
    }

    private static readonly string[] PathHooks = ["OnAfterInstallFastCallback", "OnAfterUpdateFastCallback", "OnBeforeUninstallFastCallback"];

    /// <summary>
    /// Phase 4 spec §11.4: the install directory goes on the user PATH from Velopack's install and update
    /// hooks and comes off in its uninstall hook - and from nowhere else. Those fast callbacks run only when
    /// Velopack starts the exe for that stage (and it exits after them); a call anywhere else could rewrite
    /// the PATH on an ordinary start. So, over every method body in the App assembly:
    /// <list type="bullet">
    /// <item><c>Main</c> registers the three hooks before <c>VelopackApp.Run</c>, each with a delegate whose
    /// target (the nearest <c>ldftn</c> before the registration) is a method of <c>Program</c> or its closures;</item>
    /// <item>the install and update targets call <c>Ensure</c> and not <c>Remove</c>, the uninstall target
    /// <c>Remove</c> and not <c>Ensure</c>;</item>
    /// <item>nothing else references <c>Ensure</c> or <c>Remove</c> - not as a call, not as a method group -
    /// and nothing but <c>Main</c> references the three targets.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void The_PATH_is_registered_only_from_the_Velopack_hooks()
    {
        Type program = App.GetType("Ntilde.Program", throwOnError: true)!;
        MethodInfo main = program.GetMethod("Main", CommandMemberFlags, StringArrayParameter)!;
        Type registration = App.GetType("Ntilde.Shell.UserPathRegistration", throwOnError: true)!;
        MethodInfo ensure = registration.GetMethod("Ensure", CommandMemberFlags)!;
        MethodInfo remove = registration.GetMethod("Remove", CommandMemberFlags)!;

        List<(OpCode Op, int Token)> mainRefs = MethodReferences(main);
        MethodBase Resolve(int token) => main.Module.ResolveMethod(token)!;
        bool IsVelopackApp(int index, string name) =>
            (mainRefs[index].Op == OpCodes.Callvirt || mainRefs[index].Op == OpCodes.Call)
            && Resolve(mainRefs[index].Token) is { } m && m.Name == name && m.DeclaringType?.FullName == "Velopack.VelopackApp";

        int run = Enumerable.Range(0, mainRefs.Count).FirstOrDefault(i => IsVelopackApp(i, "Run"), -1);
        Assert.True(run >= 0, "App Program.Main no longer calls VelopackApp.Run.");
        var targets = new Dictionary<string, MethodBase>();
        foreach (string hook in PathHooks)
        {
            int site = Enumerable.Range(0, mainRefs.Count).FirstOrDefault(i => IsVelopackApp(i, hook), -1);
            Assert.True(site >= 0 && site < run, $"App Program.Main must register VelopackApp.{hook} before VelopackApp.Run.");
            int ftn = mainRefs.FindLastIndex(site, r => r.Op == OpCodes.Ldftn);
            Assert.True(ftn >= 0, $"No delegate target found before VelopackApp.{hook} in App Program.Main.");
            MethodBase target = Resolve(mainRefs[ftn].Token);
            Assert.True(target.DeclaringType == program || target.DeclaringType?.DeclaringType == program,
                $"VelopackApp.{hook}'s delegate is {target.DeclaringType?.FullName}.{target.Name}, not Program's own closure.");
            targets[hook] = target;
        }

        // Every reference in the assembly to Ensure, Remove or a hook target, by the method that makes it.
        var ensureRefs = new HashSet<MethodBase>();
        var removeRefs = new HashSet<MethodBase>();
        var targetRefs = new HashSet<MethodBase>();
        var targetTokens = targets.Values.Select(t => t.MetadataToken).ToHashSet();
        foreach (MethodBase method in AllMethodBodies(App))
        {
            foreach ((_, int token) in MethodReferences(method))
            {
                if (token == ensure.MetadataToken) ensureRefs.Add(method);
                if (token == remove.MetadataToken) removeRefs.Add(method);
                if (targetTokens.Contains(token)) targetRefs.Add(method);
            }
        }

        MethodBase install = targets["OnAfterInstallFastCallback"], update = targets["OnAfterUpdateFastCallback"], uninstall = targets["OnBeforeUninstallFastCallback"];
        Assert.True(ensureRefs.SetEquals([install, update]),
            "UserPathRegistration.Ensure must be called from the install and update hooks' delegates and nowhere else; it is referenced by: " + Describe(ensureRefs));
        Assert.True(removeRefs.SetEquals([uninstall]),
            "UserPathRegistration.Remove must be called from the uninstall hook's delegate and nowhere else; it is referenced by: " + Describe(removeRefs));
        Assert.True(targetRefs.SetEquals([main]),
            "The hooks' delegate targets must be referenced only by Program.Main, which registers them; they are referenced by: " + Describe(targetRefs));
    }

    /// <summary>
    /// Phase 5 Task 21 review fix 2: on a Windows install the local daemon runs from its own copy outside the install
    /// root, so uninstalling no longer stops it by itself. Velopack's uninstall hook does - <c>MuxUninstall.Run</c> stops
    /// the daemon and removes the copies - and nothing else calls it: anywhere else it would end the user's shells on an
    /// ordinary start. Found as <see cref="The_PATH_is_registered_only_from_the_Velopack_hooks"/> finds the hook's target.
    /// </summary>
    [Fact]
    public void The_uninstall_hook_stops_the_multiplexer_and_removes_its_copies()
    {
        Type program = App.GetType("Ntilde.Program", throwOnError: true)!;
        MethodInfo main = program.GetMethod("Main", CommandMemberFlags, StringArrayParameter)!;
        MethodInfo run = App.GetType("Ntilde.Shell.Mux.MuxUninstall", throwOnError: true)!.GetMethod("Run", CommandMemberFlags)!;

        List<(OpCode Op, int Token)> mainRefs = MethodReferences(main);
        MethodBase Resolve(int token) => main.Module.ResolveMethod(token)!;
        int site = Enumerable.Range(0, mainRefs.Count).FirstOrDefault(
            i => (mainRefs[i].Op == OpCodes.Callvirt || mainRefs[i].Op == OpCodes.Call)
                && Resolve(mainRefs[i].Token) is { Name: "OnBeforeUninstallFastCallback" } m && m.DeclaringType?.FullName == "Velopack.VelopackApp",
            -1);
        Assert.True(site >= 0, "App Program.Main no longer registers VelopackApp.OnBeforeUninstallFastCallback.");
        int ftn = mainRefs.FindLastIndex(site, r => r.Op == OpCodes.Ldftn);
        Assert.True(ftn >= 0, "No delegate target found before VelopackApp.OnBeforeUninstallFastCallback in App Program.Main.");
        MethodBase uninstall = Resolve(mainRefs[ftn].Token);

        var runRefs = AllMethodBodies(App).Where(m => MethodReferences(m).Exists(r => r.Token == run.MetadataToken)).ToHashSet();
        Assert.True(runRefs.SetEquals([uninstall]),
            "MuxUninstall.Run must be called from the uninstall hook's delegate and nowhere else; it is referenced by: " + Describe(runRefs));
    }

    private static string Describe(IEnumerable<MethodBase> methods) =>
        string.Join(", ", methods.Select(m => $"{m.DeclaringType?.FullName}.{m.Name}").DefaultIfEmpty("<nothing>"));

    /// <summary>Every method and constructor in <paramref name="assembly"/> that has an IL body, compiler-generated ones included.</summary>
    private static IEnumerable<MethodBase> AllMethodBodies(Assembly assembly)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }

        return types
            .SelectMany(t => t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
            .Where(m => m.GetMethodBody() is not null);
    }

    /// <summary>
    /// Phase 4 spec §12.4: <c>ntilde-mux</c> has no verbs of its own. Its <c>Main</c> hands every
    /// argument to <c>Ntilde.Mux.Cli.MuxCli.Execute</c>, so a verb added there reaches the remote binary
    /// and the App's <c>ntilde mux</c> alike, and the two cannot drift into separate dispatch tables.
    /// The compiled call is the evidence; the source line only says where to look when it fails.
    /// </summary>
    [Fact]
    public void Mux_daemon_dispatches_every_verb_through_MuxCli()
    {
        string program = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ntilde.Mux.Daemon/Program.cs"));
        Assert.Contains("MuxCli.Execute(", program, StringComparison.Ordinal);

        MethodInfo main = Assembly.Load("ntilde-mux").GetType("Ntilde.MuxDaemon.Program", throwOnError: true)!
            .GetMethod("Main", CommandMemberFlags, StringArrayParameter)!;
        Assert.Contains(CalledMethods(main),
            m => m.Name == "Execute" && m.DeclaringType?.FullName == "Ntilde.Mux.Cli.MuxCli");
    }

    /// <summary>
    /// Phase 4 final review F3: <c>ntilde-mux</c> serves a root of its own, never the GUI's. Its <c>Main</c>
    /// resolves its paths through <c>MuxPaths.Standalone()</c>; <c>MuxPaths.Default()</c> - the app-data root the
    /// GUI's daemon serves - would put both daemons on one descriptor, socket, lock and log on a host that runs
    /// both. The App's <c>ntilde mux</c> adapter, in turn, keeps the GUI's root and never asks for ntilde-mux's.
    /// </summary>
    [Fact]
    public void Mux_daemon_serves_its_own_root_and_the_App_adapter_the_GUIs()
    {
        MethodInfo main = Assembly.Load("ntilde-mux").GetType("Ntilde.MuxDaemon.Program", throwOnError: true)!
            .GetMethod("Main", CommandMemberFlags, StringArrayParameter)!;
        List<MethodBase> mainCalls = CalledMethods(main);
        Assert.Contains(mainCalls, IsMuxPaths("Standalone"));
        Assert.DoesNotContain(mainCalls, IsMuxPaths("Default"));

        MethodInfo adapterHost = typeof(Ntilde.Shell.Mux.MuxCommand).GetMethod("CreateHost", CommandMemberFlags)!;
        List<MethodBase> adapterCalls = CalledMethods(adapterHost);
        Assert.Contains(adapterCalls, m => m.Name == "GetRootDirectory" && m.DeclaringType?.FullName == "Ntilde.Mux.Contracts.MuxDiscovery");
        Assert.DoesNotContain(adapterCalls, IsMuxPaths("Standalone"));
    }

    private static Predicate<MethodBase> IsMuxPaths(string name) =>
        m => m.Name == name && m.DeclaringType?.FullName == "Ntilde.Mux.Daemon.MuxPaths";

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(op => op.Value);

    /// <summary>The methods <paramref name="method"/>'s body calls, in IL order.</summary>
    private static List<MethodBase> CalledMethods(MethodInfo method) =>
        MethodReferences(method)
            .Where(r => r.Op == OpCodes.Call || r.Op == OpCodes.Callvirt)
            .Select(r => method.Module.ResolveMethod(r.Token)!)
            .ToList();

    /// <summary>
    /// Every instruction in <paramref name="method"/>'s body that names a method - call, callvirt, newobj,
    /// ldftn (a lambda or method group becoming a delegate), ldvirtftn, jmp, and ldtoken of a method - with
    /// its metadata token, in IL order. Unresolved, so a whole assembly can be scanned cheaply: within one
    /// module a non-generic method is always named by its own MethodDef token.
    /// </summary>
    private static List<(OpCode Op, int Token)> MethodReferences(MethodBase method)
    {
        var refs = new List<(OpCode Op, int Token)>();
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) return refs;
        int i = 0;
        while (i < il.Length)
        {
            bool twoByte = il[i] == 0xFE;
            short value = twoByte ? unchecked((short)(0xFE00 | il[i + 1])) : il[i];
            i += twoByte ? 2 : 1;
            OpCode op = OpCodesByValue[value];
            if (op.OperandType is OperandType.InlineMethod or OperandType.InlineTok)
                refs.Add((op, BitConverter.ToInt32(il, i)));
            i += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, i)),
                _ => 4,
            };
        }
        return refs;
    }
}
