using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AltTabEverywhere.Interop;

// Undocumented shell interfaces (twinui.pcshell.dll, hosted in explorer.exe). These are what the
// built-in Alt+Tab, Task View and taskbar use to decide which windows to list.
//
// WARNING: undocumented. GUIDs and vtable layouts have changed between Windows releases. Only the
// methods up to GetShowInSwitchers are used, and those were verified on Windows 11 build 26200
// (slots further down, e.g. IsTray, differ on that build and crash). Calls go through the vtable
// (see Com): every failure comes back as an HRESULT.
//
//   IServiceProvider:            3 QueryService
//   IApplicationViewCollection:  3 GetViews, 4 GetViewsByZOrder
//   IObjectArray:                3 GetCount, 4 GetAt
//   IApplicationView (IInspectable: 3-5): 9 GetThumbnailWindow, 17 GetAppUserModelId,
//                                23 GetLastActivationTimestamp, 27 GetShowInSwitchers

/// <summary>A window the shell says belongs in Alt+Tab.</summary>
internal readonly record struct ShellView(IntPtr Hwnd, string? AppUserModelId, ulong LastActivation);

/// <summary>Reads the shell's Alt+Tab list. Returns null if the interfaces aren't available.</summary>
internal sealed unsafe class ShellViews
{
    private static readonly Guid ImmersiveShellClsid = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid ServiceProviderIid = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    private static readonly Guid CollectionIid = new("1841C6D7-4F9D-42C0-AF41-8747538F10E5");
    private static readonly Guid ViewIid = new("372E1D3B-38D3-42E4-A15B-8AB2B178F513");

    /// <summary>IApplicationViewCollection, kept between calls (one connection per app run).</summary>
    private nint _collection;

    public List<ShellView>? GetSwitcherViews()
    {
        // One retry with a fresh connection covers Explorer having restarted since the last call.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (_collection == 0) _collection = Connect();
                if (_collection == 0) return null;

                var result = Read(_collection);
                if (result is not null) return result;
            }
            catch (Exception ex)
            {
                Log.Error("shell view list", ex);
            }
            Com.Release(_collection);
            _collection = 0;
        }
        return null;
    }

    private static nint Connect()
    {
        nint provider = Com.Create(ImmersiveShellClsid, ServiceProviderIid);
        if (provider == 0) return 0;
        try
        {
            Guid iid = CollectionIid;
            nint collection;
            var queryService = (delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, nint*, int>)Com.Slot(provider, 3);
            return queryService(provider, &iid, &iid, &collection) == 0 ? collection : 0;
        }
        finally
        {
            Com.Release(provider);
        }
    }

    private static List<ShellView>? Read(nint collection)
    {
        nint array;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Com.Slot(collection, 4))(collection, &array) != 0 || array == 0) return null;
        try
        {
            int count;
            if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Com.Slot(array, 3))(array, &count) != 0) return null;
            var getAt = (delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int>)Com.Slot(array, 4);

            var views = new List<ShellView>(count);
            Guid viewIid = ViewIid;
            for (int i = 0; i < count; i++)
            {
                nint view;
                if (getAt(array, i, &viewIid, &view) != 0 || view == 0) continue;
                try
                {
                    if (ReadView(view) is { } shellView) views.Add(shellView);
                }
                finally
                {
                    Com.Release(view);
                }
            }

            // Windows' Alt+Tab orders by last activation, not Z-order. They differ once you've
            // used windows that sit behind others. OrderByDescending is stable, so ties keep Z-order.
            return views.OrderByDescending(v => v.LastActivation).ToList();
        }
        finally
        {
            Com.Release(array);
        }
    }

    private static ShellView? ReadView(nint view)
    {
        int show;
        if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Com.Slot(view, 27))(view, &show) != 0 || show == 0) return null;
        IntPtr hwnd;
        if (((delegate* unmanaged[Stdcall]<nint, IntPtr*, int>)Com.Slot(view, 9))(view, &hwnd) != 0 || hwnd == IntPtr.Zero) return null;

        string? aumid = null;
        char* id;
        if (((delegate* unmanaged[Stdcall]<nint, char**, int>)Com.Slot(view, 17))(view, &id) == 0 && id != null)
        {
            aumid = new string(id);
            Marshal.FreeCoTaskMem((nint)id);
        }
        ulong lastActivation;
        if (((delegate* unmanaged[Stdcall]<nint, ulong*, int>)Com.Slot(view, 23))(view, &lastActivation) != 0) lastActivation = 0;
        return new ShellView(hwnd, aumid, lastActivation);
    }
}
