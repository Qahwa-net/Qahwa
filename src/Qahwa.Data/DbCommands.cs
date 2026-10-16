using System.Data;
using System.Data.Common;

namespace Qahwa.Data;

internal static class DbCommands
{
    /// <summary>Creates a command whose positional values are bound as parameters p0..pN (never inlined).</summary>
    public static DbCommand Create(DbConnection connection, string sql, IReadOnlyList<object?> values)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        // SQL Server's driver sends a DateTime as the old 3-millisecond "datetime" unless told otherwise, which would never equal a stored
        // microsecond value (and breaks "changed by someone else" checks). Say what each value really is. Other drivers need nothing.
        var sqlServer = command.GetType().FullName == "Microsoft.Data.SqlClient.SqlCommand";
        for (var i = 0; i < values.Count; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"p{i}";
            var value = values[i];
            if (sqlServer)
            {
                switch (value)
                {
                    case DateTime:
                        parameter.DbType = DbType.DateTime2;
                        break;
                    case DateOnly date:
                        parameter.DbType = DbType.Date;
                        value = date.ToDateTime(TimeOnly.MinValue);
                        break;
                    case TimeOnly time:
                        parameter.DbType = DbType.Time;
                        value = time.ToTimeSpan();
                        break;
                }
            }

            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }
}
