#if NETSTANDARD2_0
// Records and init-only setters need this marker type, which only exists from .NET 5 onward.
// Declaring it internally lets the same model compile for netstandard2.0 targets.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
#endif
