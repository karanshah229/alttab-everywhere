using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AltTabEverywhere.Interop;

/// <summary>
/// UI Automation (screen readers) for windows drawn without a UI framework: the provider interfaces,
/// declared for .NET's COM source generator so they work under NativeAOT, and UIAutomationCore's
/// functions. Interface pointers are passed as raw <see cref="nint"/>s and converted by hand
/// (<see cref="Uia.Pointer{T}"/>), which keeps reference counting explicit.
/// </summary>
[GeneratedComInterface, Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c")]
internal partial interface IRawElementProviderSimple
{
    int get_ProviderOptions();
    nint GetPatternProvider(int patternId);
    UiaVariant GetPropertyValue(int propertyId);
    nint get_HostRawElementProvider();
}


/// <summary>
/// A COM VARIANT holding what a provider returns (nothing, an int, a bool or a string), laid out as
/// Windows expects (24 bytes on 64-bit, 16 on 32-bit). The caller of GetPropertyValue owns a string.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UiaVariant
{
    public ushort Type;
    private ushort _reserved1, _reserved2, _reserved3;
    public nint Value;
    private nint _extra;

    private const ushort VT_I4 = 3, VT_BSTR = 8, VT_BOOL = 11;

    public static UiaVariant Of(int value) => new() { Type = VT_I4, Value = (nint)(uint)value };
    public static UiaVariant Of(bool value) => new() { Type = VT_BOOL, Value = value ? 0xFFFF : 0 }; // VARIANT_TRUE is -1 (16 bits)
    public static UiaVariant Of(string value) => new() { Type = VT_BSTR, Value = Marshal.StringToBSTR(value) };

    public readonly int AsInt() => Type == VT_I4 ? (int)(uint)Value : 0;
    public readonly bool AsBool() => Type == VT_BOOL && (Value & 0xFFFF) != 0;
    public readonly string? AsString() => Type == VT_BSTR ? Marshal.PtrToStringBSTR(Value) : null;

    /// <summary>Frees a string (for a caller that took ownership).</summary>
    public void Clear()
    {
        if (Type == VT_BSTR && Value != 0) Marshal.FreeBSTR(Value);
        this = default;
    }
}
[StructLayout(LayoutKind.Sequential)]
internal struct UiaRect
{
    public double Left, Top, Width, Height;
}

[GeneratedComInterface, Guid("f7063da8-8359-439c-9297-bbc5299a7d87")]
internal partial interface IRawElementProviderFragment
{
    nint Navigate(int direction);
    nint GetRuntimeId();
    UiaRect get_BoundingRectangle();
    nint GetEmbeddedFragmentRoots();
    void SetFocus();
    nint get_FragmentRoot();
}

[GeneratedComInterface, Guid("620ce2a5-ab8f-40a9-86cb-de3c75599b58")]
internal partial interface IRawElementProviderFragmentRoot
{
    nint ElementProviderFromPoint(double x, double y);
    nint GetFocus();
}

internal static unsafe class Uia
{
    public const int RootObjectId = -25;          // WM_GETOBJECT's lParam when UIA asks
    public const int ProviderOptionsServerSide = 0x1;
    public const int AppendRuntimeId = 3;
    public const int ElementNotAvailable = unchecked((int)0x80040201);

    public const int NavigateParent = 0, NavigateNextSibling = 1, NavigatePreviousSibling = 2, NavigateFirstChild = 3, NavigateLastChild = 4;

    public const int ControlTypeProperty = 30003, NameProperty = 30005, HasKeyboardFocusProperty = 30008,
        IsKeyboardFocusableProperty = 30009, IsEnabledProperty = 30010, AutomationIdProperty = 30011, ClassNameProperty = 30012,
        IsControlElementProperty = 30016, IsContentElementProperty = 30017, IsOffscreenProperty = 30022, FrameworkIdProperty = 30024,
        LiveSettingProperty = 30135, PositionInSetProperty = 30152, SizeOfSetProperty = 30153;

    public const int ListItemControlType = 50007, ListControlType = 50008, TextControlType = 50020, WindowControlType = 50032;
    public const int LiveAssertive = 2;

    public const int FocusChangedEvent = 20005, LiveRegionChangedEvent = 20024;

    /// <summary>An AddRef'd COM pointer for <paramref name="provider"/> as interface <typeparamref name="T"/> (0 for null).</summary>
    public static nint Pointer<T>(T? provider) where T : class =>
        provider is null ? 0 : (nint)ComInterfaceMarshaller<T>.ConvertToUnmanaged(provider);

    /// <summary>A SAFEARRAY of ints (a runtime id), owned by the caller.</summary>
    public static nint IntArray(params int[] values)
    {
        nint array = SafeArrayCreateVector(3 /* VT_I4 */, 0, (uint)values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            int value = values[i];
            SafeArrayPutElement(array, &i, &value);
        }
        return array;
    }

    [DllImport("uiautomationcore.dll")] public static extern nint UiaReturnRawElementProvider(nint hwnd, nint wParam, nint lParam, nint provider);
    [DllImport("uiautomationcore.dll")] public static extern int UiaHostProviderFromHwnd(nint hwnd, nint* provider);
    [DllImport("uiautomationcore.dll")] public static extern int UiaRaiseAutomationEvent(nint provider, int eventId);
    [DllImport("uiautomationcore.dll")] public static extern int UiaClientsAreListening();
    [DllImport("uiautomationcore.dll")] public static extern int UiaDisconnectProvider(nint provider);
    [DllImport("oleaut32.dll")] private static extern nint SafeArrayCreateVector(ushort type, int lowerBound, uint count);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayPutElement(nint array, int* index, void* value);
}
