using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace SupraChat.WebViewAvaloniaExtensionProbe;

// Synthetic compatibility probe only. Private reflection MUST NOT be adopted
// as a product API without a separate design/security/upgrade decision.
internal static class ComEnvironmentProbe
{
    private static readonly Guid EnvironmentIid = new("B96D755E-0319-4E92-A296-23436F46A1FC");
    internal static IntPtr TryAcquire(CoreWebView2Environment wrapper, out string classification)
    {
        classification = "NO_KNOWN_RAW_NATIVE_INTERFACE";
        for (Type? t = wrapper.GetType(); t is not null; t = t.BaseType)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var p in t.GetProperties(flags))
            {
                if (!p.Name.Contains("RawNativeInterface", StringComparison.Ordinal) ||
                    p.GetIndexParameters().Length != 0) continue;
                try
                {
                    if (FromComObject(p.GetValue(wrapper)) is var ptr && ptr != IntPtr.Zero)
                    { classification = "RAW_INTERFACE_PROPERTY"; return ptr; }
                }
                catch { classification = "RAW_INTERFACE_PROPERTY_REFUSED"; }
            }
            foreach (var f in t.GetFields(flags))
            {
                if (!f.FieldType.Name.Contains("ICoreWebView2Environment", StringComparison.Ordinal)) continue;
                try
                {
                    if (FromComObject(f.GetValue(wrapper)) is var ptr && ptr != IntPtr.Zero)
                    { classification = "RAW_INTERFACE_FIELD"; return ptr; }
                }
                catch { classification = "RAW_INTERFACE_FIELD_REFUSED"; }
            }
        }
        return IntPtr.Zero;
    }
    private static IntPtr FromComObject(object? native)
    {
        if (native is null || !Marshal.IsComObject(native)) return IntPtr.Zero;
        var iunknown = Marshal.GetIUnknownForObject(native);
        try
        {
            var iid = EnvironmentIid;
            if (Marshal.QueryInterface(iunknown, ref iid, out var ptr) < 0) return IntPtr.Zero;
            return ptr; // caller owns one COM reference and MUST release it
        }
        finally { Marshal.Release(iunknown); }
    }
}
