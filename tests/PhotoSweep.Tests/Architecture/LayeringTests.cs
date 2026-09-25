using System.Reflection;

namespace PhotoSweep.Tests.Architecture;

// Guards the layering rule from CLAUDE.md: Core and Presentation must never depend on WPF or WinForms,
// otherwise view-models stop being testable on plain net10.0.
public class LayeringTests
{
    private static readonly string[] UiAssemblies =
    [
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "System.Windows.Forms",
    ];

    [Theory]
    [InlineData("PhotoSweep.Core")]
    [InlineData("PhotoSweep.Presentation")]
    public void Assembly_does_not_reference_UI_frameworks(string assemblyName)
    {
        var referenced = Assembly.Load(assemblyName)
            .GetReferencedAssemblies()
            .Select(a => a.Name);

        Assert.Empty(referenced.Intersect(UiAssemblies));
    }

    [Fact]
    public void Core_does_not_reference_Presentation()
    {
        var referenced = Assembly.Load("PhotoSweep.Core")
            .GetReferencedAssemblies()
            .Select(a => a.Name);

        Assert.DoesNotContain("PhotoSweep.Presentation", referenced);
    }
}
