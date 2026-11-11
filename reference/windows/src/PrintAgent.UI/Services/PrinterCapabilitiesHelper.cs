using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PrintAgent.Common.Models;

namespace PrintAgent.UI.Services;

// Reads printer capabilities via the Win32 WinSpool API (no System.Printing /
// ReachFramework — that stack is unsupported in a Windows Service context).
//
// Critical: DeviceCapabilities is called with a REAL DEVMODE obtained from
// DocumentProperties (not null), so paper/bin options reflect the driver's
// actual configuration rather than only its startup defaults. The DEVMODE also
// carries driver-private data, so its size must be queried first.
public static class PrinterCapabilitiesHelper
{
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "DocumentPropertiesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DocumentProperties(
        IntPtr hwnd, IntPtr hPrinter, string pDeviceName,
        IntPtr pDevModeOutput, IntPtr pDevModeInput, int fMode);

    [DllImport("winspool.drv", EntryPoint = "DeviceCapabilitiesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DeviceCapabilities(
        string pDevice, string pPort, short fwCapability, IntPtr pOutput, IntPtr pDevMode);

    private const int DM_OUT_BUFFER = 2;

    // fwCapability values (wingdi.h)
    private const short DC_FIELDS = 1;
    private const short DC_PAPERS = 2;
    private const short DC_PAPERSIZE = 3;
    private const short DC_BINS = 6;
    private const short DC_DUPLEX = 7;
    private const short DC_BINNAMES = 12;
    private const short DC_ENUMRESOLUTIONS = 13;
    private const short DC_PAPERNAMES = 16;
    private const short DC_COPIES = 18;
    private const short DC_COLLATE = 22;
    private const short DC_COLORDEVICE = 32;

    private const int BIN_NAME_LEN = 24;   // fixed-length WCHAR buffers
    private const int PAPER_NAME_LEN = 64;

    public static PrinterCapabilities? Get(string printerName, string portName, Action<string>? warn = null)
    {
        if (string.IsNullOrEmpty(printerName)) return null;

        var hPrinter = IntPtr.Zero;
        var pDevMode = IntPtr.Zero;
        var port = portName ?? string.Empty;

        try
        {
            if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero)) return null;

            // Obtain a driver-filled DEVMODE (size query → allocate → fill). This
            // pointer is passed to EVERY DeviceCapabilities call below so options
            // reflect the driver's real config, not just its startup defaults.
            // (DocumentProperties may need PRINTER_ACCESS_USE on some drivers;
            //  default OpenPrinter is normally sufficient for local queues.)
            int needed = DocumentProperties(IntPtr.Zero, hPrinter, printerName, IntPtr.Zero, IntPtr.Zero, 0);
            if (needed > 0)
            {
                pDevMode = Marshal.AllocHGlobal(needed);
                int rc = DocumentProperties(IntPtr.Zero, hPrinter, printerName, pDevMode, IntPtr.Zero, DM_OUT_BUFFER);
                if (rc < 0)
                {
                    Marshal.FreeHGlobal(pDevMode);
                    pDevMode = IntPtr.Zero; // fall back to defaults-only rather than fail
                }
            }

            if (pDevMode == IntPtr.Zero)
                warn?.Invoke($"DEVMODE unavailable for '{printerName}' — capabilities read from driver defaults only");

            var caps = new PrinterCapabilities
            {
                // Scalars — the return value itself is the answer (no output buffer).
                Duplex = DeviceCapabilities(printerName, port, DC_DUPLEX, IntPtr.Zero, pDevMode) == 1,
                Color = DeviceCapabilities(printerName, port, DC_COLORDEVICE, IntPtr.Zero, pDevMode) == 1,
                Collate = DeviceCapabilities(printerName, port, DC_COLLATE, IntPtr.Zero, pDevMode) == 1,
            };
            int copies = DeviceCapabilities(printerName, port, DC_COPIES, IntPtr.Zero, pDevMode);
            caps.MaxCopies = copies > 0 ? copies : 1;
            int fields = DeviceCapabilities(printerName, port, DC_FIELDS, IntPtr.Zero, pDevMode);
            caps.Fields = fields > 0 ? fields : 0;

            // Bins: DC_BINS (WORD ids) + DC_BINNAMES (24-char names), index-paired.
            var binIds = ReadWordArray(printerName, port, DC_BINS, pDevMode);
            var binNames = ReadStringArray(printerName, port, DC_BINNAMES, BIN_NAME_LEN, pDevMode);
            for (int i = 0; i < binNames.Count; i++)
                caps.Bins.Add(new PrinterBin { Id = i < binIds.Count ? binIds[i] : 0, Name = binNames[i] });

            // Papers: DC_PAPERS (ids) + DC_PAPERNAMES (64-char) + DC_PAPERSIZE (POINT, 0.1mm).
            var paperIds = ReadWordArray(printerName, port, DC_PAPERS, pDevMode);
            var paperNames = ReadStringArray(printerName, port, DC_PAPERNAMES, PAPER_NAME_LEN, pDevMode);
            var paperSizes = ReadPointArray(printerName, port, DC_PAPERSIZE, pDevMode);
            for (int i = 0; i < paperNames.Count; i++)
                caps.Papers.Add(new PrinterPaper
                {
                    Id = i < paperIds.Count ? paperIds[i] : 0,
                    Name = paperNames[i],
                    Width = i < paperSizes.Count ? paperSizes[i].x : 0,
                    Height = i < paperSizes.Count ? paperSizes[i].y : 0,
                });

            caps.Dpis = ReadResolutions(printerName, port, pDevMode);
            return caps;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pDevMode != IntPtr.Zero) Marshal.FreeHGlobal(pDevMode);
            if (hPrinter != IntPtr.Zero) ClosePrinter(hPrinter);
        }
    }

    // Two-stage buffer pattern for array-returning capabilities.
    private static List<int> ReadWordArray(string printer, string port, short cap, IntPtr devmode)
    {
        var result = new List<int>();
        try
        {
            int count = DeviceCapabilities(printer, port, cap, IntPtr.Zero, devmode);
            if (count <= 0) return result;
            var buf = Marshal.AllocHGlobal(count * sizeof(short));
            try
            {
                int got = DeviceCapabilities(printer, port, cap, buf, devmode);
                for (int i = 0; i < got; i++)
                    result.Add((ushort)Marshal.ReadInt16(buf, i * sizeof(short)));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { /* partial result */ }
        return result;
    }

    private static List<string> ReadStringArray(string printer, string port, short cap, int charsPerName, IntPtr devmode)
    {
        var result = new List<string>();
        try
        {
            int count = DeviceCapabilities(printer, port, cap, IntPtr.Zero, devmode);
            if (count <= 0) return result;
            var buf = Marshal.AllocHGlobal(count * charsPerName * 2); // WCHAR = 2 bytes
            try
            {
                int got = DeviceCapabilities(printer, port, cap, buf, devmode);
                for (int i = 0; i < got; i++)
                {
                    var s = Marshal.PtrToStringUni(IntPtr.Add(buf, i * charsPerName * 2), charsPerName) ?? string.Empty;
                    int nul = s.IndexOf('\0');
                    if (nul >= 0) s = s.Substring(0, nul);
                    result.Add(s.Trim());
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { /* partial result */ }
        return result;
    }

    private static List<(int x, int y)> ReadPointArray(string printer, string port, short cap, IntPtr devmode)
    {
        var result = new List<(int, int)>();
        try
        {
            int count = DeviceCapabilities(printer, port, cap, IntPtr.Zero, devmode);
            if (count <= 0) return result;
            var buf = Marshal.AllocHGlobal(count * 8); // POINT = 2 * int32
            try
            {
                int got = DeviceCapabilities(printer, port, cap, buf, devmode);
                for (int i = 0; i < got; i++)
                    result.Add((Marshal.ReadInt32(buf, i * 8), Marshal.ReadInt32(buf, i * 8 + 4)));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { /* partial result */ }
        return result;
    }

    private static List<string> ReadResolutions(string printer, string port, IntPtr devmode)
    {
        var result = new List<string>();
        try
        {
            int count = DeviceCapabilities(printer, port, DC_ENUMRESOLUTIONS, IntPtr.Zero, devmode);
            if (count <= 0) return result;
            var buf = Marshal.AllocHGlobal(count * 8); // each resolution = 2 LONGs (x, y)
            try
            {
                int got = DeviceCapabilities(printer, port, DC_ENUMRESOLUTIONS, buf, devmode);
                for (int i = 0; i < got; i++)
                    result.Add($"{Marshal.ReadInt32(buf, i * 8)}x{Marshal.ReadInt32(buf, i * 8 + 4)}");
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { /* partial result */ }
        return result;
    }
}
