#if NETSTANDARD2_0
// Netstandard2.0 has no built-in System.Runtime.CompilerServices.IsExternalInit; the C# compiler only
// needs a type of this exact name/namespace to exist (it's a marker, not a real runtime dependency), so
// this local definition is enough to let `init`-only properties (used by this project's positional
// records) compile under netstandard2.0. Guarded so it never collides with the real BCL type when this
// project is ever built against a TFM that already provides it.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
