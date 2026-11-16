using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;

namespace ObjectRemoverPlugin;

/// <summary>
/// Small, thread-safe, never-throwing file logger.
/// Log file: %LocalAppData%\paint.net-shared\logs\ObjectRemover.log (rotated to ObjectRemover.old.log at ~1 MB).
/// Users can attach this file to a bug report.
/// </summary>
internal static class PluginLog
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();
    private static bool headerWritten;

    public static string LogDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "paint.net-shared", "logs");

    public static string FilePath => System.IO.Path.Combine(LogDir, "ObjectRemover.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : message + Environment.NewLine + ex);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(LogDir);
                RotateIfNeeded();

                var sb = new StringBuilder();
                if (!headerWritten)
                {
                    headerWritten = true;
                    sb.AppendLine();
                    sb.AppendLine("=================================================================");
                    sb.AppendLine($"ObjectRemover plugin v{typeof(PluginLog).Assembly.GetName().Version}");
                    sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
                    sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
                    sb.AppendLine($"Culture: {System.Globalization.CultureInfo.CurrentCulture.Name}");
                    sb.AppendLine($"Models folder: {ModelStore.SharedDir}");
                    sb.AppendLine("=================================================================");
                }

                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(level).Append(' ')
                  .Append('[').Append(Environment.CurrentManagedThreadId).Append("] ").AppendLine(message);

                System.IO.File.AppendAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never break the plugin (read-only disk, antivirus lock, ...)
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var fi = new System.IO.FileInfo(FilePath);
            if (fi.Exists && fi.Length > MaxBytes)
            {
                System.IO.File.Move(FilePath, System.IO.Path.Combine(LogDir, "ObjectRemover.old.log"), overwrite: true);
            }
        }
        catch
        {
        }
    }

    /// <summary>The last <paramref name="maxChars"/> characters of the log, for "Copy details".</summary>
    public static string ReadTail(int maxChars = 8000)
    {
        try
        {
            lock (Gate)
            {
                if (!System.IO.File.Exists(FilePath))
                {
                    return "(no log file yet)";
                }

                using var fs = new System.IO.FileStream(FilePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
                long start = Math.Max(0, fs.Length - maxChars * 2L);
                fs.Seek(start, System.IO.SeekOrigin.Begin);
                using var sr = new System.IO.StreamReader(fs, Encoding.UTF8);
                string text = sr.ReadToEnd();
                return text.Length > maxChars ? text.Substring(text.Length - maxChars) : text;
            }
        }
        catch (Exception ex)
        {
            return "(couldn't read log: " + ex.Message + ")";
        }
    }

    /// <summary>The innermost exception message, e.g. the SocketException under an HttpRequestException.</summary>
    public static string RootMessage(Exception ex)
    {
        Exception e = ex;
        while (e.InnerException != null)
        {
            e = e.InnerException;
        }

        return e.GetType().Name + ": " + e.Message;
    }

    /// <summary>A plain-language guess at what a network exception means, or "" if there is no useful hint.</summary>
    public static string NetworkHint(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            switch (e)
            {
                case AuthenticationException:
                    return "A secure (HTTPS) connection could not be established. A firewall, proxy or antivirus that inspects HTTPS traffic is a likely cause (its certificate isn't trusted).";
                case SocketException se when se.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData:
                    return "The server name could not be resolved (DNS). A DNS filter or offline network is a likely cause.";
                case SocketException se when se.SocketErrorCode == SocketError.ConnectionRefused:
                    return "The connection was refused. A firewall is likely blocking this server.";
                case SocketException se when se.SocketErrorCode == SocketError.TimedOut:
                    return "The connection timed out. A firewall is likely silently dropping traffic to this server.";
                case SocketException se when se.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted:
                    return "The connection was closed by the network. A firewall or proxy may have cut it off.";
                case HttpRequestException { StatusCode: System.Net.HttpStatusCode.ProxyAuthenticationRequired }:
                    return "Your proxy requires authentication.";
            }
        }

        return "";
    }
}
