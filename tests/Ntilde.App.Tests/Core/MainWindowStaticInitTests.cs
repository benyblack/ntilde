using System.Linq;
using System.Reflection;
using Avalonia.Media;
using Xunit;

namespace Ntilde.Tests.Core;

/// <summary>
/// Creating a <see cref="Geometry"/> needs Avalonia's render platform. A static field that builds
/// one in MainWindow's type initializer throws for whatever first touches a MainWindow static
/// before the platform is up - a plain [Fact] using one of its static helpers, say - and a type
/// whose initializer has thrown is unusable for the rest of the process: every later MainWindow
/// fails with TypeInitializationException. Which test gets there first depends on order, so this
/// checks the rule itself rather than hoping to reproduce the order.
/// </summary>
public sealed class MainWindowStaticInitTests
{
    [Fact]
    public void MainWindow_initializes_no_static_geometry()
    {
        var offenders = typeof(Ntilde.MainWindow)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.IsInitOnly && typeof(Geometry).IsAssignableFrom(field.FieldType))
            .Select(field => field.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Build these lazily (a nullable static plus a ??= property): " + string.Join(", ", offenders));
    }
}
