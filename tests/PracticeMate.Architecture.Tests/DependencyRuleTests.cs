using System.Reflection;
using PracticeMate.App.ViewModels;
using PracticeMate.Audio.Windows;
using PracticeMate.Core;
using PracticeMate.Testing.Reference;

namespace PracticeMate.Architecture.Tests;

public class DependencyRuleTests
{
    private static readonly string[] UiOrWindowsAssemblies =
    [
        "PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml",
        "System.Windows.Forms", "NAudio", "NAudio.Wasapi", "NAudio.Core",
        "PracticeMate.Audio.Windows", "PracticeMate",
    ];

    private static readonly string[] ForbiddenDspLibraries = ["MathNet.Numerics", "FftSharp", "NWaves"];

    private static Assembly AssemblyOf(string marker) => marker switch
    {
        nameof(CoreAssembly) => typeof(CoreAssembly).Assembly,
        nameof(ViewModelsAssembly) => typeof(ViewModelsAssembly).Assembly,
        nameof(AudioWindowsAssembly) => typeof(AudioWindowsAssembly).Assembly,
        nameof(ReferenceAssembly) => typeof(ReferenceAssembly).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(marker), marker, "未知的組件標記"),
    };

    [Theory]
    [InlineData(nameof(CoreAssembly))]
    [InlineData(nameof(ViewModelsAssembly))]
    public void Pure_assemblies_do_not_reference_UI_or_Windows_specific_assemblies(string marker)
    {
        var refs = AssemblyInspector.ReferencedAssemblyNames(AssemblyOf(marker));
        var violations = refs.Where(r => UiOrWindowsAssemblies.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.True(violations.Count == 0, $"{marker} 引用了禁止的組件：{string.Join(", ", violations)}");
    }

    [Fact]
    public void Core_does_not_reference_ViewModels()
    {
        var refs = AssemblyInspector.ReferencedAssemblyNames(typeof(CoreAssembly).Assembly);
        Assert.DoesNotContain("PracticeMate.App.ViewModels", refs);
    }

    [Fact]
    public void Reference_implementations_are_independent_of_production_code()
    {
        var refs = AssemblyInspector.ReferencedAssemblyNames(typeof(ReferenceAssembly).Assembly);
        var violations = refs.Where(r => r.StartsWith("PracticeMate.", StringComparison.Ordinal)).ToList();
        Assert.True(violations.Count == 0, $"參考實作不得引用正式程式碼：{string.Join(", ", violations)}");
    }

    [Theory]
    [InlineData(nameof(CoreAssembly))]
    [InlineData(nameof(ViewModelsAssembly))]
    [InlineData(nameof(AudioWindowsAssembly))]
    [InlineData(nameof(ReferenceAssembly))]
    public void No_assembly_references_third_party_DSP_libraries(string marker)
    {
        var refs = AssemblyInspector.ReferencedAssemblyNames(AssemblyOf(marker));
        var violations = refs
            .Where(r => ForbiddenDspLibraries.Any(f => r.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.True(violations.Count == 0, $"{marker} 引用了禁止的 DSP 套件：{string.Join(", ", violations)}");
    }

    [Fact]
    public void Core_references_only_base_class_libraries()
    {
        var refs = AssemblyInspector.ReferencedAssemblyNames(typeof(CoreAssembly).Assembly);
        var violations = refs
            .Where(r => !(r.StartsWith("System", StringComparison.Ordinal) || r is "netstandard" or "mscorlib"))
            .Where(r => !r.StartsWith("System.Windows", StringComparison.Ordinal))
            .ToList();
        Assert.True(violations.Count == 0, $"Core 引用了非 BCL 組件：{string.Join(", ", violations)}");
    }
}
