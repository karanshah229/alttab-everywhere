using System;
using System.Runtime.InteropServices;

namespace AltTabEverywhere.Interop;

/// <summary>
/// COM by hand: objects are raw interface pointers and methods are called through their vtable slot
/// (slot 0-2 are IUnknown's). No runtime COM support is needed, so it works under NativeAOT, and
/// every reference is released exactly where it's taken.
/// </summary>
internal static unsafe class Com
{
    private const uint CLSCTX_SERVER = 0x15; // in-process, local and remote servers

    /// <summary>A new object of class <paramref name="clsid"/> as interface <paramref name="iid"/>, or 0.</summary>
    public static nint Create(Guid clsid, Guid iid) => Create(clsid, iid, out _);

    /// <summary>The same, with CoCreateInstance's HRESULT for the log.</summary>
    public static nint Create(Guid clsid, Guid iid, out int hr)
    {
        nint obj;
        hr = CoCreateInstance(&clsid, 0, CLSCTX_SERVER, &iid, &obj);
        return hr == 0 ? obj : 0;
    }

    /// <summary>Method <paramref name="index"/> of the object's vtable (cast it to the method's signature).</summary>
    public static void* Slot(nint obj, int index) => (*(void***)obj)[index];

    public static void Release(nint obj)
    {
        if (obj != 0) Marshal.Release(obj);
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* obj);
}
