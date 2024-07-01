namespace Driver;

using Spectre.Console;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

public static partial class ConsoleUtils
{
    //private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -10;
    //private const int STD_ERROR_HANDLE = -10;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 8;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr hConsoleHandle, ref uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    public static bool SetVTMode(out int error)
    {
        error = 0;

        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
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

public static class Extensions
{
    public static bool TryGetLayout(this Layout @this, string layout_name, [NotNullWhen(true)] out Layout? layout)
    {
        layout = null;

        try
        {
            layout = @this[layout_name];
        }
        catch { }

        return layout != null;
    }
}