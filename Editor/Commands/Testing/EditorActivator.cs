using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEditorInternal;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VeryFS.UnityMCP.Editor.Commands.Testing
{
    public interface IEditorActivator
    {
        void ActivateIfNeeded();
    }

    internal sealed class UnityEditorActivator : IEditorActivator
    {
        public void ActivateIfNeeded()
        {
            if (Application.isBatchMode || InternalEditorUtility.isApplicationActive)
            {
                return;
            }

            try
            {
                PlatformEditorActivator.Activate();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Unity MCP: failed to activate Editor before test run. " + ex.Message);
            }
        }
    }

    internal static class PlatformEditorActivator
    {
        public static void Activate()
        {
#if UNITY_EDITOR_OSX
            MacEditorActivator.Activate();
#elif UNITY_EDITOR_WIN
            WindowsEditorActivator.Activate();
#endif
        }
    }

#if UNITY_EDITOR_OSX
    internal static class MacEditorActivator
    {
        private const string LibObjC = "/usr/lib/libobjc.A.dylib";

        public static void Activate()
        {
            var nsApplication = objc_getClass("NSApplication");
            if (nsApplication == IntPtr.Zero)
            {
                return;
            }

            var sharedApplication = objc_msgSend_IntPtr(
                nsApplication, sel_registerName("sharedApplication"));
            if (sharedApplication == IntPtr.Zero)
            {
                return;
            }

            objc_msgSend_Bool(
                sharedApplication,
                sel_registerName("activateIgnoringOtherApps:"),
                true);
        }

        [DllImport(LibObjC)]
        private static extern IntPtr objc_getClass(string name);

        [DllImport(LibObjC)]
        private static extern IntPtr sel_registerName(string name);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_Bool(
            IntPtr receiver,
            IntPtr selector,
            [MarshalAs(UnmanagedType.I1)] bool value);
    }
#endif

#if UNITY_EDITOR_WIN
    internal static class WindowsEditorActivator
    {
        private const int SwRestore = 9;

        public static void Activate()
        {
            var handle = Process.GetCurrentProcess().MainWindowHandle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            ShowWindow(handle, SwRestore);
            SetForegroundWindow(handle);
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
#endif
}
