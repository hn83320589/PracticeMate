using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace PracticeMate.Architecture.Tests;

/// <summary>以 System.Reflection.Metadata 讀取組件的引用（不需載入相依組件）。</summary>
internal static class AssemblyInspector
{
    public static IReadOnlyList<string> ReferencedAssemblyNames(Assembly assembly)
    {
        using var pe = new PEReader(File.OpenRead(assembly.Location));
        var md = pe.GetMetadataReader();
        return md.AssemblyReferences
            .Select(h => md.GetString(md.GetAssemblyReference(h).Name))
            .ToList();
    }

    /// <summary>回傳組件引用的所有外部型別完整名稱（巢狀型別以 + 連接）及其所屬組件。</summary>
    public static IReadOnlyList<(string Assembly, string FullName)> ReferencedTypes(Assembly assembly)
    {
        using var pe = new PEReader(File.OpenRead(assembly.Location));
        var md = pe.GetMetadataReader();
        return md.TypeReferences.Select(h => Resolve(md, h)).ToList();
    }

    private static (string Assembly, string FullName) Resolve(MetadataReader md, TypeReferenceHandle handle)
    {
        var tr = md.GetTypeReference(handle);
        var name = md.GetString(tr.Name);
        var ns = md.GetString(tr.Namespace);
        var qualified = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        switch (tr.ResolutionScope.Kind)
        {
            case HandleKind.TypeReference:
                var (asm, outer) = Resolve(md, (TypeReferenceHandle)tr.ResolutionScope);
                return (asm, outer + "+" + name);
            case HandleKind.AssemblyReference:
                var ar = md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope);
                return (md.GetString(ar.Name), qualified);
            default:
                return (string.Empty, qualified);
        }
    }
}
