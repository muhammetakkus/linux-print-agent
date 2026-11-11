using System.Runtime.InteropServices;

namespace PrintAgent.UI.Services;

public static class RawPrintHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOCINFOW
    {
        public string DocName;
        public string? OutputFile;
        public string DataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string printerName, out IntPtr hPrinter, IntPtr pd);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr hPrinter, int level, ref DOCINFOW di);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    public static (bool ok, string? error) SendBytes(string printerName, byte[] data, int copies, string jobName)
    {
        if (string.IsNullOrEmpty(printerName))
            return (false, "Printer name is empty");
        if (data == null || data.Length == 0)
            return (false, "Empty payload");
        if (copies < 1) copies = 1;

        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
            return (false, $"OpenPrinter failed (Win32 {Marshal.GetLastWin32Error()})");

        try
        {
            var di = new DOCINFOW
            {
                DocName = string.IsNullOrEmpty(jobName) ? "Nuxprint Raw Job" : jobName,
                OutputFile = null,
                DataType = "RAW",
            };

            var pBytes = Marshal.AllocCoTaskMem(data.Length);
            try
            {
                Marshal.Copy(data, 0, pBytes, data.Length);

                for (var c = 0; c < copies; c++)
                {
                    if (StartDocPrinter(hPrinter, 1, ref di) == 0)
                        return (false, $"StartDocPrinter failed (Win32 {Marshal.GetLastWin32Error()})");

                    var pageStarted = false;
                    try
                    {
                        if (!StartPagePrinter(hPrinter))
                            return (false, $"StartPagePrinter failed (Win32 {Marshal.GetLastWin32Error()})");
                        pageStarted = true;

                        if (!WritePrinter(hPrinter, pBytes, data.Length, out var written) || written != data.Length)
                            return (false, $"WritePrinter wrote {written}/{data.Length} bytes (Win32 {Marshal.GetLastWin32Error()})");
                    }
                    finally
                    {
                        if (pageStarted) EndPagePrinter(hPrinter);
                        EndDocPrinter(hPrinter);
                    }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(pBytes);
            }

            return (true, null);
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }
}
