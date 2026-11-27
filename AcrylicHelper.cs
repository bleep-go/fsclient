using System;
using System.Runtime.InteropServices;

namespace FsClient;

/// <summary>
/// Включает системный acrylic для окна: всё, что находится за окном,
/// размывается DWM без собственной копии обоев.
/// </summary>
internal static class AcrylicHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr DataPointer;
        public uint DataSize;
    }

    private const int WcaAccentPolicy = 19;
    private const int AccentEnableAcrylicBlurBehind = 4;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowCompositionAttribute(
        IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <param name="gradientColor">ABGR: Alpha, Blue, Green, Red.</param>
    public static void Enable(IntPtr hwnd, int gradientColor)
    {
        var policy = new AccentPolicy
        {
            AccentState = AccentEnableAcrylicBlurBehind,
            AccentFlags = 0,
            GradientColor = gradientColor,
            AnimationId = 0
        };

        int size = Marshal.SizeOf(policy);
        IntPtr pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                DataPointer = pointer,
                DataSize = (uint)size
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }
}
