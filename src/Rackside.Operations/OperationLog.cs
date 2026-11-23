using System.Globalization;
using Rackside.Core.Localization;
using Rackside.Core.Security;

namespace Rackside.Operations;

/// <summary>
/// The operation log: "yyyy-MM-dd HH:mm:ss [LEVEL] message", translated and masked, appended to
/// every file in <see cref="Paths"/> and raised to the UI. Levels: INFO, OK, WARN, ERROR.
/// </summary>
public sealed class OperationLog(Translator t, SecretMasker masker)
{
    private readonly object gate = new();

    /// <summary>Files the lines go to (the operation log.txt and, where used, the general one).</summary>
    public List<string> Paths { get; } = [];

    /// <summary>Every line written (for the Log tab).</summary>
    public event Action<string>? Line;

    public void Write(string level, string message)
    {
        var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " [" + level + "] " + masker.Protect(t.T(message));
        lock (gate)
        {
            foreach (var path in Paths.Where(p => !string.IsNullOrEmpty(p)))
            {
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.AppendAllText(path, line + Environment.NewLine, new System.Text.UTF8Encoding(false));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        Line?.Invoke(line);
    }

    public void Info(string message) => Write("INFO", message);

    public void Ok(string message) => Write("OK", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);
}

/// <summary>What an operation needs from the window: warnings and questions, and the status bar.</summary>
public interface IOperatorUi
{
    /// <summary>Strong warning. True only if the operator accepts.</summary>
    bool Warn(string text, string title);

    /// <summary>Yes/no question.</summary>
    bool Ask(string text, string title);

    /// <summary>Status bar text and optional progress (0-100).</summary>
    void Status(string text, int? progress = null);
}

/// <summary>Username, password and enable for one operation. Never saved; registered in the masker.</summary>
public sealed record Credentials(string User, string Password, string Enable);
