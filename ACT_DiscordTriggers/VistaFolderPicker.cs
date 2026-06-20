using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ACT_DiscordTriggers {
  /// <summary>
  /// Opens the modern shell folder picker (the Explorer-style <c>IFileOpenDialog</c>
  /// with <c>FOS_PICKFOLDERS</c>) — the dialog Windows draws in its current chrome
  /// (Win11 look on Windows 11). net48's <see cref="System.Windows.Forms.FolderBrowserDialog"/>
  /// only renders the legacy tree, so this is a dependency-free COM-interop wrapper.
  ///
  /// Pure interop (system types only), so it's load-invariant-safe: nothing here
  /// derives from a Costura-merged dependency.
  /// </summary>
  internal static class VistaFolderPicker {
    /// <summary>
    /// Show the picker and return the chosen folder, or <c>null</c> if the user
    /// cancelled. <paramref name="owner"/> is the parent window handle (for modality),
    /// <paramref name="initialPath"/> seeds the starting location when it exists.
    /// Throws on shells without <c>IFileOpenDialog</c> (pre-Vista) — callers should
    /// fall back to the classic dialog.
    /// </summary>
    public static string PickFolder(IntPtr owner, string title, string initialPath) {
      var dialog = (IFileOpenDialog)new FileOpenDialogRcw();
      try {
        dialog.GetOptions(out var options);
        dialog.SetOptions(options | FOS.FOS_PICKFOLDERS | FOS.FOS_FORCEFILESYSTEM);
        if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);

        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath)) {
          try {
            var shellItemGuid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(initialPath, IntPtr.Zero, ref shellItemGuid, out var seed);
            if (seed != null) dialog.SetFolder(seed);
          } catch {
            // Best-effort seed: a bad initial path must not block the picker.
          }
        }

        // Show returns S_OK (0) on accept; cancel is HRESULT_FROM_WIN32(ERROR_CANCELLED).
        if (dialog.Show(owner) != 0) return null;
        dialog.GetResult(out var result);
        result.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
        return path;
      } finally {
        Marshal.FinalReleaseComObject(dialog);
      }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
      [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
      IntPtr pbc,
      [In] ref Guid riid,
      [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [Flags]
    private enum FOS : uint {
      FOS_PICKFOLDERS = 0x00000020,
      FOS_FORCEFILESYSTEM = 0x00000040,
    }

    private enum SIGDN : uint {
      SIGDN_FILESYSPATH = 0x80058000,
    }

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRcw { }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem {
      void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
      void GetParent(out IShellItem ppsi);
      void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
      void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
      void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    // IFileOpenDialog : IFileDialog : IModalWindow — the full vtable must be declared
    // in inheritance order so the slots line up, even though we only call a handful.
    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog {
      // IModalWindow
      [PreserveSig] int Show(IntPtr parent);
      // IFileDialog
      void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
      void SetFileTypeIndex(uint iFileType);
      void GetFileTypeIndex(out uint piFileType);
      void Advise(IntPtr pfde, out uint pdwCookie);
      void Unadvise(uint dwCookie);
      void SetOptions(FOS fos);
      void GetOptions(out FOS pfos);
      void SetDefaultFolder(IShellItem psi);
      void SetFolder(IShellItem psi);
      void GetFolder(out IShellItem ppsi);
      void GetCurrentSelection(out IShellItem ppsi);
      void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
      void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
      void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
      void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
      void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
      void GetResult(out IShellItem ppsi);
      void AddPlace(IShellItem psi, int fdap);
      void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
      void Close([MarshalAs(UnmanagedType.Error)] int hr);
      void SetClientGuid([In] ref Guid guid);
      void ClearClientData();
      void SetFilter(IntPtr pFilter);
      // IFileOpenDialog
      void GetResults(out IntPtr ppenum);
      void GetSelectedItems(out IntPtr ppsai);
    }
  }
}
