using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// The only durable log the desktop app has. Before this there was none: LogMessages lived in
/// memory, <c>LogToTrace()</c> writes nowhere in a published build, and the three Debug.WriteLine
/// calls are compiled out in Release — so after a crash there was nothing at all to read.
///
/// Deliberately small: a plain rolling text file, no logging framework, no dependency. It records
/// unhandled exceptions and anything the app explicitly considers worth keeping.
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    private const long MaxBytes = 2 * 1024 * 1024;
    private const int KeptGenerations = 3;

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CryptoAITerminal", "logs");

    public static string FilePath { get; } = Path.Combine(Directory, "terminal.log");

    /// <summary>
    /// Installs handlers for the three ways an exception escapes in an Avalonia + Rx app. Without
    /// them an unhandled exception on a background thread ends the process with no trace, and a
    /// faulted Task that nobody awaited disappears entirely.
    /// </summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Write("FATAL", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
            // Nothing else to do — the runtime is tearing the process down either way.
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("UNOBSERVED", e.Exception);
            // Observing it keeps a stray fire-and-forget from killing the process.
            e.SetObserved();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Write("INFO", "process exit");
    }

    public static void Write(string level, Exception ex) =>
        Write(level, ex.ToString());

    public static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                Roll();
                File.AppendAllText(
                    FilePath,
                    $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z  {level,-10}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // A logger that throws is worse than no logger.
        }
    }

    private static void Roll()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length < MaxBytes) return;

        var oldest = $"{FilePath}.{KeptGenerations}";
        if (File.Exists(oldest)) File.Delete(oldest);

        for (var i = KeptGenerations - 1; i >= 1; i--)
        {
            var from = $"{FilePath}.{i}";
            if (File.Exists(from)) File.Move(from, $"{FilePath}.{i + 1}", overwrite: true);
        }

        File.Move(FilePath, $"{FilePath}.1", overwrite: true);
    }
}
