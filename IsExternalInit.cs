using System.ComponentModel;

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Reserved for compiler use to enable init-only setters and records in C# 9.0+
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal class IsExternalInit : Attribute
    {
    }
}
