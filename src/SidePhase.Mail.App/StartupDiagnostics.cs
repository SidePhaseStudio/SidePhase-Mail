using System.Runtime.InteropServices;
using System.Text;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.App;

/// <summary>
/// Makes start-up failures visible: writes them to %LOCALAPPDATA%\SidePhase Mail\Logs\startup-error.log and shows a
/// native message box, so the app never just silently fails to open.
/// </summary>
internal static class StartupDiagnostics
{
    private const uint MB_OK = 0x0;
    private const uint MB_ICONERROR = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    public static string LogFile => Path.Combine(new AppDataPaths().LogsFolder, "startup-error.log");

    /// <summary>Records a non-fatal problem in Logs\app.log (never message contents or credentials).</summary>
    public static void Log(string what, Exception exception)
    {
        try
        {
            var file = Path.Combine(new AppDataPaths().LogsFolder, "app.log");
            Append(file, $"{DateTimeOffset.Now:O} {what}: {exception.GetType().FullName}: {exception.Message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never fail the caller.
        }
    }

    /// <summary>Largest a log file grows before it's rotated to .old (one previous file is kept).</summary>
    private const long MaxLogBytes = 1024 * 1024;

    /// <summary>
    /// Appends to a log with secrets and email addresses removed (tokens, passwords, codes; see LogRedactor), and
    /// keeps each log small.
    /// </summary>
    internal static void Append(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var info = new FileInfo(file);
        if (info.Exists && info.Length > MaxLogBytes) File.Move(file, file + ".old", overwrite: true);
        File.AppendAllText(file, Core.Formatting.LogRedactor.Redact(text));
    }

    public static void Report(string stage, Exception exception, bool showDialog = true)
    {
        var details = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("O")).Append(" — failed during ").AppendLine(stage)
            .Append("OS: ").Append(Environment.OSVersion.VersionString)
            .Append(", ").Append(RuntimeInformation.OSArchitecture)
            .Append(", process ").AppendLine(RuntimeInformation.ProcessArchitecture.ToString())
            .AppendLine(exception.ToString())
            .AppendLine()
            .ToString();

        try
        {
            Append(LogFile, details);
        }
        catch (Exception)
        {
            // Nothing else we can do; the message box below still reports the problem.
        }

        Console.Error.WriteLine(Core.Formatting.LogRedactor.Redact(details));

        if (showDialog && Environment.GetEnvironmentVariable("SIDEPHASE_NO_ERROR_DIALOG") != "1")
        {
            MessageBoxW(0,
                $"SidePhase Mail couldn't start ({stage}).\n\n{exception.GetType().Name}: {Core.Formatting.LogRedactor.Redact(exception.Message)}\n\nDetails were saved to:\n{LogFile}",
                "SidePhase Mail", MB_OK | MB_ICONERROR);
        }
    }
}
