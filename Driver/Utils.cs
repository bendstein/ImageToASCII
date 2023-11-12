using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Driver;

using System.Runtime.InteropServices;

public static class ConsoleUtils
{
    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -10;
    private const int STD_ERROR_HANDLE = -10;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 8;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, ref uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    public static bool SetVTMode(out int error)
    {
        error = 0;

        if(Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            IntPtr std_out_handle = GetStdHandle(STD_OUTPUT_HANDLE);

            if (std_out_handle == INVALID_HANDLE_VALUE)
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            uint mode = ENABLE_VIRTUAL_TERMINAL_PROCESSING;
            if (!GetConsoleMode(std_out_handle, ref mode))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            mode = ENABLE_VIRTUAL_TERMINAL_PROCESSING;

            if (!SetConsoleMode(std_out_handle, mode))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            return true;
        }

        return false;
    }
}