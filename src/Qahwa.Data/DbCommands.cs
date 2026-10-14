using System.Data.Common;

namespace Qahwa.Data;

internal static class DbCommands
{
    /// <summary>Creates a command whose positional values are bound as parameters p0..pN (never inlined).</summary>
    public static DbCommand Create(DbConnection connection, string sql, IReadOnlyList<object?> values)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < values.Count; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"p{i}";
            parameter.Value = values[i] ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }
}
