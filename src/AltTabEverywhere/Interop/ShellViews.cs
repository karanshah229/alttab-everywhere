using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AltTabEverywhere.Interop;

// Undocumented shell interfaces (twinui.pcshell.dll, hosted in explorer.exe). These are what the
// built-in Alt+Tab, Task View and taskbar use to decide which windows to list.
//
// WARNING: undocumented. GUIDs and vtable layouts have changed between Windows releases. Only the
// methods up to GetShowInSwitchers are declared, and those were verified on Windows 11 build 26200
// (slots further down, e.g. IsTray, differ on that build and crash). Every method uses PreserveSig
// so a failing HRESULT comes back as a value instead of an exception.

[ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IObjectArray
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProvider10
{
    [PreserveSig] int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
}

// IApplicationView derives from IInspectable; .NET can't marshal InterfaceIsIInspectable, so the
// three IInspectable slots are declared by hand.
[ComImport, Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationView
{
    [PreserveSig] int GetIids(out int count, out IntPtr iids);
    [PreserveSig] int GetRuntimeClassName(out IntPtr name);
    [PreserveSig] int GetTrustLevel(out int level);

    [PreserveSig] int SetFocus();
    [PreserveSig] int SwitchTo();
    [PreserveSig] int TryInvokeBack(IntPtr callback);
    [PreserveSig] int GetThumbnailWindow(out IntPtr hwnd);
    [PreserveSig] int GetMonitor(out IntPtr immersiveMonitor);
    [PreserveSig] int GetVisibility(out int visibility);
    [PreserveSig] int SetCloak(int cloakType, int unknown);
    [PreserveSig] int GetPosition(ref Guid guid, out IntPtr position);
    [PreserveSig] int SetPosition(ref IntPtr position);
    [PreserveSig] int InsertAfterWindow(IntPtr hwnd);
    [PreserveSig] int GetExtendedFramePosition(out RECT rect);
    [PreserveSig] int GetAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int SetAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int IsEqualByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out int result);
    [PreserveSig] int GetViewState(out uint state);
    [PreserveSig] int SetViewState(uint state);
    [PreserveSig] int GetNeediness(out int neediness);
    [PreserveSig] int GetLastActivationTimestamp(out ulong timestamp);
    [PreserveSig] int SetLastActivationTimestamp(ulong timestamp);
    [PreserveSig] int GetVirtualDesktopId(out Guid guid);
    [PreserveSig] int SetVirtualDesktopId(ref Guid guid);
    [PreserveSig] int GetShowInSwitchers(out int flag);
}

[ComImport, Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationViewCollection
{
    [PreserveSig] int GetViews(out IObjectArray array);
    [PreserveSig] int GetViewsByZOrder(out IObjectArray array);
}

/// <summary>A window the shell says belongs in Alt+Tab.</summary>
internal readonly record struct ShellView(IntPtr Hwnd, string? AppUserModelId, ulong LastActivation);

/// <summary>Reads the shell's Alt+Tab list. Returns null if the interfaces aren't available.</summary>
internal sealed class ShellViews
{
    private static readonly Guid ImmersiveShellClsid = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");

    private IApplicationViewCollection? _collection;

    public List<ShellView>? GetSwitcherViews()
    {
        // One retry with a fresh connection covers Explorer having restarted since the last call.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                _collection ??= Connect();
                if (_collection is null) return null;

                var result = Read(_collection);
                if (result is not null) return result;
            }
            catch (Exception ex)
            {
                Log.Error("shell view list", ex);
            }
            _collection = null;
        }
        return null;
    }

    private static IApplicationViewCollection? Connect()
    {
        var shellType = Type.GetTypeFromCLSID(ImmersiveShellClsid, throwOnError: false);
        if (shellType is null || Activator.CreateInstance(shellType) is not IServiceProvider10 provider) return null;

        Guid iid = typeof(IApplicationViewCollection).GUID;
        return provider.QueryService(ref iid, ref iid, out object service) == 0
            ? service as IApplicationViewCollection
            : null;
    }

    private static List<ShellView>? Read(IApplicationViewCollection collection)
    {
        if (collection.GetViewsByZOrder(out var array) != 0 || array is null) return null;
        if (array.GetCount(out int count) != 0) return null;

        var views = new List<ShellView>(count);
        Guid viewIid = typeof(IApplicationView).GUID;
        for (int i = 0; i < count; i++)
        {
            if (array.GetAt(i, ref viewIid, out object obj) != 0 || obj is not IApplicationView view) continue;
            if (view.GetShowInSwitchers(out int show) != 0 || show == 0) continue;
            if (view.GetThumbnailWindow(out IntPtr hwnd) != 0 || hwnd == IntPtr.Zero) continue;

            string? aumid = view.GetAppUserModelId(out string id) == 0 ? id : null;
            ulong lastActivation = view.GetLastActivationTimestamp(out ulong ts) == 0 ? ts : 0;
            views.Add(new ShellView(hwnd, aumid, lastActivation));
        }

        // Windows' Alt+Tab orders by last activation, not Z-order. They differ once you've
        // used windows that sit behind others. OrderByDescending is stable, so ties keep Z-order.
        return views.OrderByDescending(v => v.LastActivation).ToList();
    }
}
