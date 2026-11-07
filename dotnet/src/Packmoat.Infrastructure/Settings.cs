namespace Packmoat.Infrastructure;

/// <summary>
/// The one place the core reads its PACKMOAT_* settings (#233). A setting comes from the variable itself, or else from a file named by the
/// same variable with <c>_FILE</c> added (a container secret, for example), with one trailing newline (<c>\n</c> or <c>\r\n</c>) removed. With no file variable the
/// value is exactly what the environment gives, unset included.
/// <para>It stops the start, naming the variable but never its value or the file's path, when both are set, when the file is missing or
/// unreadable, or when the file is empty. An empty variable counts as not set for either one.</para>
/// <para>Not registered in DI: the composition root and the admin tool read through <see cref="Get"/>; tests make their own.</para>
/// </summary>
public sealed class Settings
{
    /// <summary>The suffix that names a setting's file variable.</summary>
    public const string FileSuffix = "_FILE";

    private static readonly Settings Process = new(Environment.GetEnvironmentVariable, File.ReadAllText);

    private readonly Func<string, string?> _environment;
    private readonly Func<string, string> _readFile;

    /// <param name="environment">Looks a variable up; null when it is not set.</param>
    /// <param name="readFile">Reads a whole file as text; throws when it cannot.</param>
    public Settings(Func<string, string?> environment, Func<string, string> readFile)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _readFile = readFile ?? throw new ArgumentNullException(nameof(readFile));
    }

    /// <summary>A setting from this process's environment (see the class remarks).</summary>
    public static string? Get(string name) => Process.Read(name);

    /// <summary>A setting from this reader's environment (see the class remarks).</summary>
    public string? Read(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var value = _environment(name);
        var fileVariable = name + FileSuffix;
        var path = _environment(fileVariable);
        if (string.IsNullOrEmpty(path)) return value;
        if (!string.IsNullOrEmpty(value))
            throw new SettingsException($"{name} and {fileVariable} are both set. Set one of them.");

        string text;
        try { text = _readFile(path); }
        // The message names the variable only: the path and the reader's own message (which names the path) are left out.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException)
        {
            throw new SettingsException($"The file named by {fileVariable} is missing or cannot be read.");
        }
        // One line ending, Unix or Windows, so a file written on either works; nothing else is trimmed.
        if (text.EndsWith("\r\n", StringComparison.Ordinal)) text = text[..^2];
        else if (text.EndsWith('\n')) text = text[..^1];
        if (text.Length == 0) throw new SettingsException($"The file named by {fileVariable} is empty.");
        return text;
    }
}

/// <summary>A setting or its file that stops the start (#233). The message names the variable, never its value or the file's path.</summary>
public sealed class SettingsException(string message) : InvalidOperationException(message)
{
    /// <summary>True when the setting is simply not set (neither the variable nor its file), so a caller that can do without it may (#238).</summary>
    public bool NotSet { get; private init; }

    /// <summary>"NAME is not set.", marked <see cref="NotSet"/>.</summary>
    public static SettingsException Unset(string name) => new($"{name} is not set.") { NotSet = true };
}
