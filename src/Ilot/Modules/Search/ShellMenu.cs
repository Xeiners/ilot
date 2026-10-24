using System.Diagnostics;
using System.Runtime.InteropServices;
using Ilot.Interop;

namespace Ilot.Modules.Search;

/// <summary>
/// The Explorer's own context menu of a file, a folder or an app: shown whole (« Afficher plus d'options »), or one of its
/// verbs run directly (open with, copy, delete, properties), so they behave exactly as in the Explorer (the Recycle Bin,
/// the user's confirmation setting, the apps that add their own entries…). On the UI thread only (COM, single-threaded).
/// </summary>
internal static class ShellMenu
{
    private const uint FirstCommand = 1;
    private const uint LastCommand = 0x7FFF;

    private static readonly Guid ShellFolderId = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid ContextMenuId = new("000214E4-0000-0000-C000-000000000046");

    /// <summary>The menu on screen: its owner-drawn submenus (Send to, Open with) need the window's messages.</summary>
    private static IContextMenu? _shown;

    /// <summary>
    /// Shows the menu of <paramref name="path"/> (a file system path, or a shell path such as "shell:AppsFolder\…") at
    /// <paramref name="at"/> (screen pixels), owned by <paramref name="hwnd"/>; <c>true</c> if one of its commands ran.
    /// </summary>
    public static bool Show(nint hwnd, string path, NativeMethods.Point at, bool extended)
    {
        using var menu = Load(hwnd, path, extended);
        if (menu is null)
        {
            return false;
        }

        _shown = menu.Handler;
        int command;
        try
        {
            command = NativeMethods.TrackPopupMenuEx(
                menu.Handle, NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON, at.X, at.Y, hwnd, 0);
        }
        finally
        {
            _shown = null;
        }

        return command >= FirstCommand && Invoke(menu.Handler, hwnd, (nint)(command - FirstCommand), at);
    }

    /// <summary>
    /// Runs <paramref name="verb"/> ("copy", "delete", "properties"…) on <paramref name="path"/>, if its menu offers it:
    /// the shell may bring the process down on a verb it does not know, so only the menu's own commands are run.
    /// </summary>
    public static bool Run(nint hwnd, string path, string verb)
    {
        using var menu = Load(hwnd, path, extended: false);
        if (menu is null || Find(menu, verb) is not { } command)
        {
            return false;
        }

        NativeMethods.GetCursorPos(out var at);
        return Invoke(menu.Handler, hwnd, command, at);
    }

    /// <summary>The Windows « Open with » dialog for the file at <paramref name="path"/>.</summary>
    public static bool OpenWith(nint hwnd, string path)
    {
        nint file = Marshal.StringToHGlobalUni(path);
        try
        {
            var info = new NativeMethods.OpenAsInfo
            {
                File = file,
                Flags = NativeMethods.OAIF_ALLOW_REGISTRATION | NativeMethods.OAIF_REGISTER_EXT | NativeMethods.OAIF_EXEC,
            };
            return NativeMethods.SHOpenWithDialog(hwnd, ref info) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(file);
        }
    }

    /// <summary>
    /// While the menu is on screen, the owner window hands it the messages its owner-drawn submenus need; <c>null</c>
    /// for any other message.
    /// </summary>
    public static nint? HandleMessage(uint message, nint wParam, nint lParam)
    {
        if (_shown is null || message is not (NativeMethods.WM_INITMENUPOPUP or NativeMethods.WM_DRAWITEM
            or NativeMethods.WM_MEASUREITEM or NativeMethods.WM_MENUCHAR))
        {
            return null;
        }

        if (_shown is IContextMenu3 third && third.HandleMenuMsg2(message, wParam, lParam, out nint result) == 0)
        {
            return result;
        }

        if (_shown is IContextMenu2 second && second.HandleMenuMsg(message, wParam, lParam) == 0)
        {
            return 0;
        }

        return null;
    }

    /// <summary>The offset of the menu's command that is <paramref name="verb"/> (its top level only); <c>null</c> if none.</summary>
    private static nint? Find(LoadedMenu menu, string verb)
    {
        const int Length = 256;
        nint buffer = Marshal.AllocHGlobal(Length * sizeof(char));
        try
        {
            int count = NativeMethods.GetMenuItemCount(menu.Handle);
            for (int i = 0; i < count; i++)
            {
                uint id = NativeMethods.GetMenuItemID(menu.Handle, i);
                if (id is < FirstCommand or > LastCommand)
                {
                    continue; // A separator or a submenu.
                }

                Marshal.WriteInt16(buffer, 0);
                nint offset = (nint)(id - FirstCommand);
                if (menu.Handler.GetCommandString(offset, NativeMethods.GCS_VERBW, 0, buffer, Length) == 0
                    && string.Equals(Marshal.PtrToStringUni(buffer), verb, StringComparison.OrdinalIgnoreCase))
                {
                    return offset;
                }
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Runs the menu's command at <paramref name="offset"/> from its first one.</summary>
    private static bool Invoke(IContextMenu handler, nint hwnd, nint offset, NativeMethods.Point at)
    {
        var info = new CommandInfo
        {
            Size = Marshal.SizeOf<CommandInfo>(),
            Mask = NativeMethods.CMIC_MASK_UNICODE | NativeMethods.CMIC_MASK_PTINVOKE,
            Hwnd = hwnd,
            Verb = offset,
            VerbW = offset,
            Show = NativeMethods.SW_SHOWNORMAL,
            Invoke = at,
        };

        int hr = handler.InvokeCommand(ref info);
        if (hr != 0)
        {
            Debug.WriteLine($"[Search] Shell command not run: 0x{hr:X8}");
        }

        return hr == 0;
    }

    /// <summary>The context menu of <paramref name="path"/>, filled; <c>null</c> if the shell does not know it.</summary>
    private static LoadedMenu? Load(nint hwnd, string path, bool extended)
    {
        if (NativeMethods.SHParseDisplayName(path, 0, out nint pidl, 0, out _) != 0 || pidl == 0)
        {
            return null;
        }

        nint folder = 0;
        nint ui = 0;
        try
        {
            var shellFolder = ShellFolderId;
            if (NativeMethods.SHBindToParent(pidl, ref shellFolder, out folder, out nint child) != 0 || folder == 0)
            {
                Marshal.FreeCoTaskMem(pidl);
                return null;
            }

            var parent = (IShellFolder)Marshal.GetObjectForIUnknown(folder);
            var contextMenu = ContextMenuId;
            if (parent.GetUIObjectOf(hwnd, 1, ref child, ref contextMenu, 0, out ui) != 0 || ui == 0)
            {
                Marshal.FreeCoTaskMem(pidl);
                return null;
            }

            var handler = (IContextMenu)Marshal.GetObjectForIUnknown(ui);
            nint handle = NativeMethods.CreatePopupMenu();
            uint flags = NativeMethods.CMF_NORMAL | (extended ? NativeMethods.CMF_EXTENDEDVERBS : 0);
            if (handler.QueryContextMenu(handle, 0, FirstCommand, LastCommand, flags) < 0)
            {
                NativeMethods.DestroyMenu(handle);
                Marshal.FreeCoTaskMem(pidl);
                return null;
            }

            return new LoadedMenu(handler, handle, pidl);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Debug.WriteLine($"[Search] No shell menu for {path}: {ex.Message}");
            Marshal.FreeCoTaskMem(pidl);
            return null;
        }
        finally
        {
            if (folder != 0)
            {
                Marshal.Release(folder);
            }

            if (ui != 0)
            {
                Marshal.Release(ui);
            }
        }
    }

    private sealed class LoadedMenu(IContextMenu handler, nint handle, nint pidl) : IDisposable
    {
        public IContextMenu Handler { get; } = handler;

        public nint Handle { get; } = handle;

        public void Dispose()
        {
            NativeMethods.DestroyMenu(Handle);
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>CMINVOKECOMMANDINFOEX.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CommandInfo
    {
        public int Size;
        public uint Mask;
        public nint Hwnd;
        public nint Verb;
        public nint Parameters;
        public nint Directory;
        public int Show;
        public uint HotKey;
        public nint Icon;
        public nint Title;
        public nint VerbW;
        public nint ParametersW;
        public nint DirectoryW;
        public nint TitleW;
        public NativeMethods.Point Invoke;
    }

    // Only the methods used are typed; the others keep their place in the table.
    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName();

        void EnumObjects();

        void BindToObject();

        void BindToStorage();

        void CompareIDs();

        void CreateViewObject();

        void GetAttributesOf();

        // One child: a reference to it is the array the shell wants (a .NET array would go as a SAFEARRAY).
        [PreserveSig]
        int GetUIObjectOf(nint hwnd, uint count, ref nint children, ref Guid riid, nint reserved, out nint ui);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);

        [PreserveSig]
        int InvokeCommand(ref CommandInfo info);

        [PreserveSig]
        int GetCommandString(nint command, uint type, nint reserved, nint name, uint length);
    }

    [ComImport]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2 : IContextMenu
    {
        [PreserveSig]
        new int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);

        [PreserveSig]
        new int InvokeCommand(ref CommandInfo info);

        [PreserveSig]
        new int GetCommandString(nint command, uint type, nint reserved, nint name, uint length);

        [PreserveSig]
        int HandleMenuMsg(uint message, nint wParam, nint lParam);
    }

    [ComImport]
    [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3 : IContextMenu2
    {
        [PreserveSig]
        new int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);

        [PreserveSig]
        new int InvokeCommand(ref CommandInfo info);

        [PreserveSig]
        new int GetCommandString(nint command, uint type, nint reserved, nint name, uint length);

        [PreserveSig]
        new int HandleMenuMsg(uint message, nint wParam, nint lParam);

        [PreserveSig]
        int HandleMenuMsg2(uint message, nint wParam, nint lParam, out nint result);
    }
}
