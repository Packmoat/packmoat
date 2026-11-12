using System.Data.Common;

namespace Packmoat.Infrastructure;

/// <summary>
/// Parameters on any engine's command, for code that holds a <see cref="DbCommand"/> (a lifecycle participant on the
/// creation or purge transaction) rather than an engine's own.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public static class DbCommandParameters
{
    /// <summary>
    /// Adds the parameter <paramref name="name"/> with <paramref name="value"/>, exactly as given: a pure pass-through, so null stays null
    /// (no DBNull), and the engine infers the type from the value as its own AddWithValue does.
    /// </summary>
    public static void AddParameter(this DbCommand command, string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(command);
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
