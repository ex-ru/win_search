using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WAI.App;

/// <summary>
/// Controls Windows DWM Acrylic/Blur composition attributes for transparent WPF windows.
/// </summary>
public static class WindowBlurHelper
{
    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private enum WindowCompositionAttribute
    {
        WCA_ACCENT_POLICY = 19
    }

    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    public static void SetBlur(Window window, bool enable)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var accent = new AccentPolicy
            {
                AccentState = enable ? AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND : AccentState.ACCENT_DISABLED,
                AccentFlags = enable ? 2 : 0,
                GradientColor = enable ? unchecked((int)0x80202020) : 0
            };

            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size
                };
                int res = SetWindowCompositionAttribute(hwnd, ref data);
                if (res != 1 && enable)
                {
                    // Fallback to simpler blur if Acrylic is not available
                    accent.AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND;
                    Marshal.StructureToPtr(accent, ptr, false);
                    SetWindowCompositionAttribute(hwnd, ref data);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch { }
    }
}
