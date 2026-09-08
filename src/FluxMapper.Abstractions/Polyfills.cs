// Compile-time-only polyfills for netstandard2.0, one of the two target frameworks this assembly
// multi-targets (alongside net10.0). Nothing here has any effect on the net10.0 build -- the
// NETSTANDARD2_0 preprocessor symbol is only defined by the SDK when TargetFramework is netstandard2.0,
// so on net10.0 this whole file compiles to nothing and the real BCL types are used instead.
#if NETSTANDARD2_0

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Marker type the compiler looks for to allow C# 9 <c>init</c>-accessor and record
    /// positional-property syntax to compile. netstandard2.0's reference assemblies don't ship it (it was
    /// added alongside C# 9 itself), so every netstandard2.0 target that uses <c>init</c> or records needs
    /// its own copy -- this one covers this assembly's own <c>Resolvers.cs</c> options records. It carries
    /// no runtime behavior; the type only needs to exist.
    /// </summary>
    internal static class IsExternalInit
    {
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>
    /// Polyfill for the real .NET 7+ BCL attribute of the same name, so <c>IMapper</c>'s AOT-safety
    /// annotations (see its Native-AOT-related members) compile under netstandard2.0 too. Declared
    /// <c>public</c> -- matching the real attribute's own accessibility -- specifically so that
    /// FluxMapper.Core, which references this assembly, resolves the same type via ordinary assembly
    /// reference rather than needing its own copy.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor, Inherited = false)]
    public sealed class RequiresDynamicCodeAttribute : Attribute
    {
        public RequiresDynamicCodeAttribute(string message) => Message = message;

        public string Message { get; }

        public string? Url { get; set; }
    }

    /// <summary>
    /// Polyfill for the real .NET 5+ BCL attribute of the same name -- see
    /// <see cref="RequiresDynamicCodeAttribute"/> for why this lives here and is <c>public</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor, Inherited = false)]
    public sealed class RequiresUnreferencedCodeAttribute : Attribute
    {
        public RequiresUnreferencedCodeAttribute(string message) => Message = message;

        public string Message { get; }

        public string? Url { get; set; }
    }
}

#endif
