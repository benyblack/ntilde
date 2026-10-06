using System.Reflection;
using System.Runtime.CompilerServices;
using NetArchTest.Rules;

namespace Ntilde.Architecture.Tests;

/// <summary>
/// Each production assembly puts its types in a namespace that matches its assembly name,
/// and no two assemblies share a namespace prefix. The App assembly is the composition
/// root: it owns the bare "Ntilde" root plus app-specific buckets (Shell, Controls,
/// Services, Models, ViewModels, Views, UI, CommandAssist) and must not reach into a leaf
/// assembly's reserved prefix.
/// </summary>
public class NamespaceAlignmentTests
{
    private static Assembly LoadByName(string name) => Assembly.Load(name);

    // Leaf assemblies, each owning exactly "Ntilde.<Name>.*". Ntilde.Launcher is ntilde.com (Phase 4
    // spec §11), which references nothing at all.
    private static readonly string[] LeafAssemblies =
        { "Ntilde.VT", "Ntilde.Replay", "Ntilde.Rendering",
          "Ntilde.Pty", "Ntilde.Platform", "Ntilde.AgentHost.Contracts", "Ntilde.Mux.Contracts",
          "Ntilde.Mux", "Ntilde.Launcher" };

    [Theory]
    [InlineData("Ntilde.VT")]
    [InlineData("Ntilde.Replay")]
    [InlineData("Ntilde.Rendering")]
    [InlineData("Ntilde.Pty")]
    [InlineData("Ntilde.Platform")]
    [InlineData("Ntilde.AgentHost.Contracts")]
    [InlineData("Ntilde.CommandAssist")]
    [InlineData("Ntilde.Mux.Contracts")]
    [InlineData("Ntilde.Mux")]
    [InlineData("Ntilde.Launcher")]
    public void Leaf_assembly_types_reside_in_its_own_namespace(string asmName)
    {
        var result = Types.InAssembly(LoadByName(asmName))
            .That()
            .DoNotResideInNamespace("System.Runtime.CompilerServices")
            .And().ArePublic()
            .Should()
            .ResideInNamespaceStartingWith(asmName)
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"{asmName} types not in {asmName}.*: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void No_two_assemblies_share_a_namespace_prefix()
    {
        // Each leaf's reserved prefix must be used by no other assembly (leaf or App).
        // The App project emits assembly name "Ntilde" (not "Ntilde.App").
        var others = new List<(string Label, string AsmName)>(
            LeafAssemblies.Select(n => (n, n)))
        {
            ("Ntilde.App", "Ntilde"),
            // CommandAssist is checked as a consumer here but is not a prefix *owner* in this loop:
            // its Views stay in the App by design, so the "one prefix, one assembly" rule is
            // asserted with that carve-out in
            // App_may_only_use_the_CommandAssist_prefix_for_Views below.
            ("Ntilde.CommandAssist", "Ntilde.CommandAssist"),
        };

        foreach (var owner in LeafAssemblies)
        {
            foreach (var (label, asmName) in others)
            {
                if (label == owner) continue;

                // A child assembly owns a sub-namespace of its parent's prefix by construction:
                // Ntilde.Mux.Contracts lives under "Ntilde.Mux". The reverse direction - the parent
                // reaching into the child's namespace - is asserted by
                // Mux_does_not_use_the_MuxContracts_namespace below.
                if (asmName.StartsWith(owner + ".", StringComparison.Ordinal)) continue;

                var result = Types.InAssembly(LoadByName(asmName))
                    .That().ArePublic()
                    .Should()
                    .NotResideInNamespaceStartingWith(owner)
                    .GetResult();

                Assert.True(result.IsSuccessful,
                    $"{label} must not use the {owner} namespace prefix. " +
                    $"Offenders: {string.Join(", ", result.FailingTypeNames ?? [])}");
            }
        }
    }

    /// <summary>
    /// <c>Ntilde.CommandAssist</c> is the only prefix deliberately shared between two
    /// assemblies: the assist assembly owns it, and the App keeps
    /// <c>Ntilde.CommandAssist.Views</c> because those are Avalonia <c>UserControl</c>s and
    /// the assist assembly must stay UI-toolkit-free. Anything else the App puts under that prefix
    /// is code that failed to move and should have.
    /// </summary>
    /// <remarks>
    /// Deliberately not filtered to public types. The archetypal leftover from an extraction is an
    /// <em>internal</em> helper the mechanical move missed, so a visibility filter here would let
    /// through exactly what this rule exists to catch. Compiler-generated types (XAML codegen,
    /// closure and iterator classes) are excluded instead: they are emitted under their declaring
    /// type's namespace, so flagging them would only restate the verdict on the type that owns
    /// them, and they are not code anyone can "move".
    /// </remarks>
    [Fact]
    public void App_may_only_use_the_CommandAssist_prefix_for_Views()
    {
        var result = Types.InAssembly(LoadByName("Ntilde"))
            .That().DoNotResideInNamespace("Ntilde.CommandAssist.Views")
            .And().DoNotHaveCustomAttribute(typeof(CompilerGeneratedAttribute))
            .Should()
            .NotResideInNamespaceStartingWith("Ntilde.CommandAssist")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "App may only own Ntilde.CommandAssist.Views; everything else under that prefix " +
            $"belongs in the CommandAssist assembly. Offenders: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Mux_does_not_use_the_MuxContracts_namespace()
    {
        var result = Types.InAssembly(LoadByName("Ntilde.Mux"))
            .That().ArePublic()
            .Should()
            .NotResideInNamespaceStartingWith("Ntilde.Mux.Contracts")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Wire types belong in Ntilde.Mux.Contracts. Offenders: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    // The standalone ntilde-mux (Phase 4 spec §10.1). Its assembly is named for the executable, so
    // the prefix it owns cannot be derived from the name the way a leaf's is above.
    private const string MuxDaemonAssembly = "ntilde-mux";
    private const string MuxDaemonNamespace = "Ntilde.MuxDaemon";

    /// <summary>
    /// <c>ntilde-mux</c> owns <c>Ntilde.MuxDaemon</c>. Not filtered to public types: its one type,
    /// <c>Program</c>, is internal, so a public filter would check nothing. Compiler-generated types
    /// are excluded for the reason given on <see cref="App_may_only_use_the_CommandAssist_prefix_for_Views"/>,
    /// and through reflection rather than NetArchTest: the list a collection expression synthesizes
    /// (<c>&lt;&gt;z__ReadOnlySingleElementList`1</c>, in the global namespace) carries the attribute,
    /// but its nested <c>Enumerator</c> does not, so only a walk up the declaring types finds it.
    /// </summary>
    [Fact]
    public void MuxDaemon_types_reside_in_the_MuxDaemon_namespace() =>
        AssertEveryTypeResidesIn(MuxDaemonAssembly, MuxDaemonNamespace, MuxDaemonNamespace + ".Program");

    /// <summary>
    /// The leaf row above checks public types only, and ntilde.com's <c>Program</c> and its P/Invokes are
    /// internal, so they are checked here the way <c>ntilde-mux</c>'s are.
    /// </summary>
    [Fact]
    public void Launcher_types_reside_in_the_Launcher_namespace() =>
        AssertEveryTypeResidesIn("Ntilde.Launcher", "Ntilde.Launcher", "Ntilde.Launcher.Program");

    private static void AssertEveryTypeResidesIn(string assemblyName, string ownedNamespace, string pinnedType)
    {
        static bool CompilerGenerated(Type? t) =>
            t is not null && (t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) || CompilerGenerated(t.DeclaringType));

        Type[] own = LoadByName(assemblyName).GetTypes()
            .Where(t => !CompilerGenerated(t) && t.Namespace != "System.Runtime.CompilerServices")
            .ToArray();

        // Pins the selection itself, so a rename cannot turn this into a check over nothing.
        Assert.Contains(own, t => t.FullName == pinnedType);

        string[] offenders = own
            .Where(t => t.Namespace != ownedNamespace && t.Namespace?.StartsWith(ownedNamespace + ".", StringComparison.Ordinal) != true)
            .Select(t => t.FullName ?? t.Name)
            .ToArray();
        Assert.True(offenders.Length == 0,
            $"{assemblyName} types not in {ownedNamespace}.*: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// Every Ntilde production assembly beside these tests - all this project references, directly or
    /// not, found by enumerating the output directory rather than listed, so a project added later is
    /// covered without an edit here. ntilde-mux itself and test assemblies are left out.
    /// </summary>
    private static string[] OtherProductionAssemblies() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "Ntilde*.dll")
            .Select(path => AssemblyName.GetAssemblyName(path).Name ?? string.Empty)
            .Where(name => name.Length > 0
                && !string.Equals(name, MuxDaemonAssembly, StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".Tests", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The reverse of the row above: no other assembly puts a type, public or not, under
    /// <c>Ntilde.MuxDaemon</c>. A string-prefix rule for <c>Ntilde.Mux</c> would let
    /// <c>Ntilde.Mux</c> itself do so unnoticed.
    /// </summary>
    [Fact]
    public void No_other_assembly_uses_the_MuxDaemon_namespace()
    {
        string[] others = OtherProductionAssemblies();

        // Pins the enumeration, so a changed output layout cannot turn this into a check over nothing:
        // the App, both console tools and every leaf must be among them.
        Assert.All(LeafAssemblies.Append("Ntilde").Append("Ntilde.CommandAssist").Append("Ntilde.Cli").Append("Ntilde.Conformance"),
            name => Assert.Contains(name, others));

        foreach (string asmName in others)
        {
            var result = Types.InAssembly(LoadByName(asmName))
                .Should()
                .NotResideInNamespaceStartingWith(MuxDaemonNamespace)
                .GetResult();

            Assert.True(result.IsSuccessful,
                $"{asmName} must not use the {MuxDaemonNamespace} namespace, which {MuxDaemonAssembly} owns. " +
                $"Offenders: {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }

    [Fact]
    public void Text_client_types_reside_in_the_TextClient_namespace()
    {
        var result = Types.InAssembly(LoadByName("Ntilde.Mux"))
            .That().HaveNameEndingWith("ConsoleSurface")
            .Or().HaveNameStartingWith("TextClient")
            .Or().HaveName("DetachChord")
            .Should()
            .ResideInNamespace("Ntilde.Mux.TextClient")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Text client types belong in Ntilde.Mux.TextClient. Offenders: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
