using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace DeltaUI
{
    /// <summary>
    /// Fizyczne rozdzielczosci monitorow podpietych do pulpitu (Windows). Unity zna tylko ten, na ktorym dziala gra,
    /// a przegladarka ma miec limit rozmiaru oparty o najwiekszy fizyczny monitor, a nie stala liczbe.
    /// </summary>
    public static class Monitors
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public uint dmFields;
            public int dmPositionX, dmPositionY;
            public uint dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE dm);

        private const int AttachedToDesktop = 0x1, CurrentSettings = -1;
        private static Vector2Int largest;
        private static bool done;

        /// <summary>Najwieksza (wg liczby pikseli) aktualna rozdzielczosc sposrod podpietych monitorow.</summary>
        public static Vector2Int Largest
        {
            get
            {
                if (!done) { done = true; Probe(); }
                return largest;
            }
        }

        private static void Probe()
        {
            largest = new Vector2Int(Screen.currentResolution.width, Screen.currentResolution.height);      // wartosc awaryjna
            try
            {
                for (uint i = 0; i < 16; i++)
                {
                    var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                    if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                    if ((dd.StateFlags & AttachedToDesktop) == 0) continue;
                    var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE)) };
                    if (!EnumDisplaySettings(dd.DeviceName, CurrentSettings, ref dm)) continue;
                    if ((long)dm.dmPelsWidth * dm.dmPelsHeight > (long)largest.x * largest.y)
                        largest = new Vector2Int((int)dm.dmPelsWidth, (int)dm.dmPelsHeight);
                }
            }
            catch (Exception ex) { Debug.LogWarning("[DeltaUI] monitors: " + ex.Message); }
        }

        /// <summary>
        /// Najwiekszy sensowny rozmiar okna z trescia (np. strony): w pelnym ekranie - rozdzielczosc najwiekszego
        /// fizycznego monitora, w oknie - rozmiar okna gry.
        /// </summary>
        public static Vector2Int MaxContent
        {
            get
            {
                if (!Screen.fullScreen) return new Vector2Int(Screen.width, Screen.height);
                Vector2Int l = Largest;
                return new Vector2Int(Mathf.Max(l.x, Screen.width), Mathf.Max(l.y, Screen.height));
            }
        }
    }
}
