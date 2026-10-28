using System.Runtime.InteropServices;
using System.Text;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.App;

/// <summary>
/// Diagnostic logs in the data folder's Logs\ (app.log, errors.log, startup-error.log), and native message boxes so
/// the app never just silently fails to open.
/// <para>
/// What is written: time, app version and channel, OS version, where it happened, exception types, their messages
/// (with tokens, passwords, codes and email addresses removed, see LogRedactor) and stack traces. What is never
/// written: message contents, subjects, attachment contents, passwords or tokens. Inner exceptions are listed by type
/// and redacted message only, because some (server replies) can quote what the server was sent. Logs stay on this PC.
/// </para>
/// </summary>
internal static class StartupDiagnostics
{
    private const uint MB_OK = 0x0;
    private const uint MB_YESNO = 0x4;
    private const uint MB_ICONERROR = 0x10;
    private const uint MB_ICONWARNING = 0x30;
    private const uint MB_DEFBUTTON2 = 0x100;
    private const int IDYES = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    public static string LogFile => Path.Combine(new AppDataPaths().LogsFolder, "startup-error.log");

    private static bool DialogsAllowed => Environment.GetEnvironmentVariable("SIDEPHASE_NO_ERROR_DIALOG") != "1";

    /// <summary>Records a non-fatal problem in Logs\app.log.</summary>
    public static void Log(string what, Exception exception)
    {
        try
        {
            var file = Path.Combine(new AppDataPaths().LogsFolder, "app.log");
            Append(file, $"{DateTimeOffset.Now:O} {what}: {Describe(exception, includeStack: false)}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never fail the caller.
        }
    }

    /// <summary>Records a step (no exception) in Logs\app.log, e.g. how a send went.</summary>
    public static void Trace(string what, string text)
    {
        try
        {
            Append(Path.Combine(new AppDataPaths().LogsFolder, "app.log"), $"{DateTimeOffset.Now:O} {what}: {text}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never fail the caller.
        }
    }

    /// <summary>Records an unexpected error that the app recovered from, with its stack trace, in Logs\errors.log.</summary>
    public static void LogError(string what, Exception exception)
    {
        try
        {
            var file = Path.Combine(new AppDataPaths().LogsFolder, "errors.log");
            Append(file, $"{DateTimeOffset.Now:O} {what} [{RuntimeSummary()}]{Environment.NewLine}{Describe(exception, includeStack: true)}{Environment.NewLine}");
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

    /// <summary>Type, redacted message and (optionally) stack trace, plus the types and messages of inner exceptions.</summary>
    internal static string Describe(Exception exception, bool includeStack)
    {
        var text = new StringBuilder()
            .Append(exception.GetType().FullName).Append(": ").Append(exception.Message);
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            text.Append(" ← ").Append(inner.GetType().FullName).Append(": ").Append(Truncate(inner.Message, 300));
        if (includeStack && exception.StackTrace is { } stack) text.AppendLine().Append(stack);
        return text.ToString();
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length] + "…";

    private static string RuntimeSummary() =>
        $"SidePhase Mail {AppIdentity.Version} ({AppIdentity.Channel}{(AppIdentity.SourceRevision is { } rev ? ", " + rev : string.Empty)}), " +
        $"{Environment.OSVersion.VersionString}, {RuntimeInformation.OSArchitecture}";

    public static void Report(string stage, Exception exception, bool showDialog = true)
    {
        var details = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("O")).Append(" — failed during ").AppendLine(stage)
            .AppendLine(RuntimeSummary())
            .Append("Process: ").AppendLine(RuntimeInformation.ProcessArchitecture.ToString())
            .AppendLine(Describe(exception, includeStack: true))
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

        if (showDialog && DialogsAllowed)
        {
            MessageBoxW(0,
                $"SidePhase Mail couldn't start ({stage}).\n\n{exception.GetType().Name}: {Core.Formatting.LogRedactor.Redact(exception.Message)}\n\nDetails were saved to:\n{LogFile}",
                "SidePhase Mail", MB_OK | MB_ICONERROR);
        }
    }

    /// <summary>Shows a native message (used before the main window exists).</summary>
    public static void ShowMessage(string text, bool warning)
    {
        if (!DialogsAllowed) return;
        MessageBoxW(0, text, "SidePhase Mail", MB_OK | (warning ? MB_ICONWARNING : MB_ICONERROR));
    }

    /// <summary>Asks a yes/no question before the main window exists. "No" is the default; false without dialogs.</summary>
    public static bool AskYesNo(string text) =>
        DialogsAllowed && MessageBoxW(0, text, "SidePhase Mail", MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2) == IDYES;
}
