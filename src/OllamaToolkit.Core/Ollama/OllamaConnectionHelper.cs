using System.Net.Http;
using System.Net.Sockets;

namespace OllamaToolkit.Core.Ollama;

public static class OllamaConnectionHelper
{
    public static bool IsConnectionError(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException)
            {
                return true;
            }

            var message = OllamaOutputSanitizer.StripAnsi(current.Message);
            if (message.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
                || message.Contains("localhost:11434", StringComparison.OrdinalIgnoreCase)
                || message.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase)
                || message.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
                || message.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
                || message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase)
                || message.Contains("llama-server", StringComparison.OrdinalIgnoreCase)
                || message.Contains("500 Internal Server Error", StringComparison.OrdinalIgnoreCase)
                || message.Contains("an error occurred while sending the request", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return ex is HttpRequestException;
    }

    public static string FormatUserMessage(Exception ex) =>
        IsConnectionError(ex)
            ? "Ollama API is not reachable at localhost:11434. The Ollama process may have crashed during the test — wait for recovery or restart Ollama from the system tray, then retry."
            : OllamaOutputSanitizer.FormatException(ex);
}