﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using dexih.functions;
using dexih.functions.Query;
using dexih.transforms;
using dexih.transforms.Exceptions;
using MySql.Data.MySqlClient;
using static Dexih.Utils.DataType.DataType;

namespace dexih.connections.sql
{
    [Connection(
        ConnectionCategory = EConnectionCategory.SqlDatabase,
        Name = "MySql", 
        Description = "MySQL is an open-source relational database management system (RDBMS) owned by Oracle Corporation",
        DatabaseDescription = "Database Name",
        ServerDescription = "Server Name",
        AllowsConnectionString = true,
        AllowsSql = true,
        AllowsFlatFiles = false,
        AllowsManagedConnection = true,
        AllowsSourceConnection = true,
        AllowsTargetConnection = true,
        AllowsUserPassword = true,
        AllowsWindowsAuth = true,
        RequiresDatabase = true,
        RequiresLocalStorage = false
        )]
    public class ConnectionMySql : ConnectionSql
    {

        public override string ServerHelp => "Server";
        public override string DefaultDatabaseHelp => "Database";
        public override bool AllowNtAuth => true;
        public override bool AllowUserPass => true;
        public override string DatabaseTypeName => "MySql";
        public override EConnectionCategory DatabaseConnectionCategory => EConnectionCategory.SqlDatabase;

        protected override string SqlDelimiterOpen { get; } = "`";
        protected override string SqlDelimiterClose { get; } = "`";

//		public override object ConvertParameterType(object value)
//		{
//            switch (value)
//            {
//                case UInt16 uint16:
//                    return (Int32)uint16;
//                case UInt32 uint32:
//                    return (Int64)uint32;
//				case UInt64 uint64:
//					return (Int64)uint64;
//				default:
//                    return value;
//            }
//		}
        
        public override object GetConnectionMaxValue(ETypeCode typeCode, int length = 0)
        {
            switch (typeCode)
            {
                case ETypeCode.DateTime:
                    return new DateTime(9999,12,31);
                case ETypeCode.Time:
                    return TimeSpan.FromDays(1) - TimeSpan.FromSeconds(1); //mysql doesn't support milliseconds
                case ETypeCode.Double:
                    return 1E+100;
                case ETypeCode.Single:
                    return 1E+37F;
                default:
                    return GetDataTypeMaxValue(typeCode, length);
            }
        }
	    
        public override object GetConnectionMinValue(ETypeCode typeCode)
        {
            switch (typeCode)
            {
                case ETypeCode.DateTime:
                    return new DateTime(1000,1,1);
                case ETypeCode.Double:
                    return -1E+100;
                case ETypeCode.Single:
                    return -1E+37F;
                default:
                    return GetDataTypeMinValue(typeCode);
            }
        }

        public override async Task ExecuteInsertBulk(Table table, DbDataReader reader, CancellationToken cancellationToken)
        {
            try
            {
                var timer = Stopwatch.StartNew();
                using (var connection = await NewConnection())
                {
                    var fieldCount = reader.FieldCount;
                    var row = new StringBuilder();

                    while (!reader.IsClosed || row.Length > 0)
                    {
                        var insert = new StringBuilder();

                        // build an sql command that looks like
                        // INSERT INTO User (FirstName, LastName) VALUES ('gary','holland'),('jack','doe'),... ;
                        insert.Append("INSERT INTO " + SqlTableName(table) + " (");

                        for (var i = 0; i < fieldCount; i++)
                        {
                            insert.Append(AddDelimiter(reader.GetName(i)) + (i < fieldCount - 1 ? "," : ")"));
                        }

                        insert.Append(" values ");

                        var isFirstRow = true;
                        
                        // if there is a cached row from previous loop, add it to the sql.
                        if (row.Length > 0)
                        {
                            insert.Append(row);
                            row.Clear();
                            isFirstRow = false;
                        }

                        while (await reader.ReadAsync(cancellationToken))
                        {
                            row.Append("(");

                            for (var i = 0; i < fieldCount; i++)
                            {
                                row.Append(GetSqlFieldValueQuote(table.Columns[i].DataType, reader[i]) +
                                           (i < fieldCount - 1 ? "," : ")"));
                            }

                            // if the maximum sql size will be exceeded with this value, then break, so the command can be executed.
                            if (insert.Length + row.Length + 2 > MaxSqlSize)
                                break;

                            if(!isFirstRow) insert.Append(",");
                            insert.Append(row);
                            row.Clear();
                            isFirstRow = false;
                        }

                        if (!isFirstRow)
                        {
                            // sql statement is going to be too large to handle, so exit.
                            if (insert.Length > MaxSqlSize)
                            {
                                throw new ConnectionException($"The generated sql was too large to execute.  The size was {(insert.Length + row.Length)} and the maximum supported by MySql is {MaxSqlSize}.  To fix this, either reduce the fields being used or increase the `max_allow_packet` variable in the MySql database.");
                            }

                            using (var cmd = new MySqlCommand(insert.ToString(), (MySqlConnection)connection))
                            {
                                cmd.CommandType = CommandType.Text;
                                try
                                {
                                    cmd.ExecuteNonQuery();
                                }
                                catch (Exception ex)
                                {
#if DEBUG
                                    throw new ConnectionException("Error running following sql command: " + insert.ToString(0, 500), ex);
#else
                                    throw new ConnectionException("Error running following sql command", ex);
#endif                                
                                }
                            
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new ConnectionException($"Bulk insert failed. {ex.Message}", ex);
            }
        }
        
        public override async Task<bool> TableExists(Table table, CancellationToken cancellationToken)
        {
            try
            {
                using (var connection = await NewConnection())
                using (var cmd = CreateCommand(connection, "SHOW TABLES LIKE @NAME"))
                {
                    cmd.Parameters.Add(CreateParameter(cmd, "@NAME", table.Name));
                    var tableExists = await cmd.ExecuteScalarAsync(cancellationToken);
                    return tableExists != null;
                }
            }
            catch(Exception ex)
            {
                throw new ConnectionException($"Table exists failed. {ex.Message}", ex);
            }
        }

        /// <summary>
        /// This creates a table in a managed database.  Only works with tables containing a surrogate key.
        /// </summary>
        /// <returns></returns>
        public override async Task CreateTable(Table table, bool dropTable, CancellationToken cancellationToken)
        {
            try
            {
                var tableExists = await TableExists(table, cancellationToken);

                //if table exists, and the dropTable flag is set to false, then error.
                if (tableExists && dropTable == false)
                {
                    throw new ConnectionException($"The table {table.Name} already exists. Drop the table first.");
                }

                //if table exists, then drop it.
                if (tableExists)
                {
                    var dropResult = await DropTable(table);
                }

                var createSql = new StringBuilder();

                //Create the table
                createSql.Append("create table " + AddDelimiter(table.Name) + " ( ");
                foreach (var col in table.Columns)
                {
                    createSql.Append(AddDelimiter(col.Name) + " " + GetSqlType(col));
                    
                    if (col.DeltaType == TableColumn.EDeltaType.AutoIncrement)
                        createSql.Append(" auto_increment");

                    createSql.Append(col.AllowDbNull == false ? " NOT NULL" : " NULL");
                    createSql.Append(",");
                }

				//Add the primary key using surrogate key or autoincrement.
				var key = table.GetDeltaColumn(TableColumn.EDeltaType.SurrogateKey) ?? table.GetDeltaColumn(TableColumn.EDeltaType.AutoIncrement);

                if (key != null)
					createSql.Append("PRIMARY KEY (" + AddDelimiter(key.Name) + "),");


				//remove the last comma
				createSql.Remove(createSql.Length - 1, 1);
                createSql.Append(")");


				using (var connection = await NewConnection())
				using (var command = connection.CreateCommand())
				{
					command.CommandText = createSql.ToString();
					try
					{
						await command.ExecuteNonQueryAsync(cancellationToken);
					}
					catch (Exception ex)
					{
                        throw new ConnectionException($"The create table query failed.  {ex.Message}");
					}
				}
            }
            catch (Exception ex)
            {
                throw new ConnectionException($"Create table failed. {ex.Message}", ex);
            }
        }

        protected override string GetSqlType(TableColumn column)
        {
            string sqlType;

            switch (column.DataType)
            {
                case ETypeCode.Byte:
                    sqlType = "tinyint unsigned";
                    break;
                case ETypeCode.SByte:
                    sqlType = "tinyint";
                    break;
                case ETypeCode.UInt16:
                    sqlType = "smallint unsigned";
                    break;
				case ETypeCode.Int16:
                    sqlType = "smallint";
                    break;
                case ETypeCode.UInt32:
                    sqlType = "int unsigned";
                    break;
                case ETypeCode.Int32:
                    sqlType = "int";
                    break;
                case ETypeCode.Int64:
                    sqlType = "bigint";
                    break;
				case ETypeCode.UInt64:
					sqlType = "bigint unsigned";
                    break;
                case ETypeCode.String:
                    if (column.MaxLength == null)
                        sqlType = (column.IsUnicode == true ? "n" : "") +  "varchar(8000)";
                    else
                        sqlType = (column.IsUnicode == true ? "n" : "") + "varchar(" + column.MaxLength + ")";
                    break;
				case ETypeCode.Text:
                case ETypeCode.Json:
                case ETypeCode.Xml:
                    sqlType = "longtext";
					break;
                case ETypeCode.Single:
                    sqlType = "real";
                    break;
                case ETypeCode.Double:
                    sqlType = "double";
                    break;
                case ETypeCode.Boolean:
                    sqlType = "bit(1)";
                    break;
                case ETypeCode.DateTime:
                    sqlType = "DateTime";
                    break;
                case ETypeCode.Time:
                    sqlType = "time";
                    break;
                case ETypeCode.Guid:
                    sqlType = "char(40)";
                    break;
                case ETypeCode.Binary:
                    sqlType = "blob";
                    break;
                case ETypeCode.Unknown:
                    sqlType = "text";
                    break;
                case ETypeCode.Decimal:
                    sqlType = $"numeric ({column.Precision??28}, {column.Scale??0})";
                    break;
                default:
                    throw new Exception($"The datatype {column.DataType} is not compatible with the create table.");
            }

            return sqlType;
        }


        /// <summary>
        /// Gets the start quote to go around the values in sql insert statement based in the column type.
        /// </summary>
        /// <returns></returns>
        protected override string GetSqlFieldValueQuote(ETypeCode type, object value)
        {
            string returnValue;

            if (value == null || value is DBNull)
                return "null";

            //if (value is string && type != ETypeCode.String && string.IsNullOrWhiteSpace((string)value))
            //    return "null";

            switch (type)
            {
                case ETypeCode.Byte:
                case ETypeCode.Single:
                case ETypeCode.Int16:
                case ETypeCode.Int32:
                case ETypeCode.Int64:
                case ETypeCode.SByte:
                case ETypeCode.UInt16:
                case ETypeCode.UInt32:
                case ETypeCode.UInt64:
                case ETypeCode.Double:
                case ETypeCode.Decimal:
                case ETypeCode.Boolean:
                    returnValue = MySqlHelper.EscapeString(value.ToString());
                    break;
                case ETypeCode.String:
				case ETypeCode.Text:
                case ETypeCode.Json:
                case ETypeCode.Xml:
                case ETypeCode.Guid:
                case ETypeCode.Unknown:
                    returnValue = "'" + MySqlHelper.EscapeString(value.ToString()) + "'";
                    break;
                case ETypeCode.DateTime:
                    if (value is DateTime)
                        returnValue = "STR_TO_DATE('" + MySqlHelper.EscapeString(((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss.ff")) + "', '%Y-%m-%d %H:%i:%s.%f')";
                    else
						returnValue = "STR_TO_DATE('"+ MySqlHelper.EscapeString((string)value) + "', '%Y-%m-%d %H:%i:%s.%f')";
                    break;
                case ETypeCode.Time:
                    if (value is TimeSpan span)
						returnValue = "TIME_FORMAT('" + MySqlHelper.EscapeString(span.ToString("c")) + "', '%H:%i:%s.%f')";
					else
                        returnValue = "TIME_FORMAT('" + MySqlHelper.EscapeString((string)value) + "', '%H:%i:%s.%f')";
					break;
                case ETypeCode.Binary:
                    returnValue = "X'" + TryParse(ETypeCode.String, value) +"'";
                    break;
                default:
                    throw new Exception("The datatype " + type + " is not compatible with the sql insert statement.");
            }

            return returnValue;
        }

        public override async Task<DbConnection> NewConnection()
        {
            MySqlConnection connection = null;

            try
            {
                string connectionString;
                if (UseConnectionString)
                    connectionString = ConnectionString;
                else
                {
                    var hostport = Server.Split(':');
                    string port;
                    if (hostport.Count() == 1)
                    {
                        port = "";
                    }
                    else
                    {
                        port = ";port=" + hostport[1];
                    }

                    if (UseWindowsAuth == false)
						connectionString = "Server=" + hostport[0] + port + "; uid=" + Username + "; pwd=" + Password + "; ";
					else
						connectionString = "Server=" + hostport[0] + port + "; IntegratedSecurity=yes; Uid=auth_windows;";

					if(!string.IsNullOrEmpty(DefaultDatabase)) 
					{
						connectionString += "Database = " + DefaultDatabase;
					}
                }

                connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();
                State = (EConnectionState)connection.State;

                if (connection.State != ConnectionState.Open)
                {
                    connection.Dispose();
                    throw new ConnectionException($"The MySql connection has a state of {connection.State}.");
                }

				// update the maximum packet size supported by this mysql database.
				using (var cmd = CreateCommand(connection, @" SELECT @@max_allowed_packet"))
				{
					var result = cmd.ExecuteScalar();
					if(result != DBNull.Value && result != null)
					{
						MaxSqlSize = Convert.ToInt64(result);	
					}
				}

                return connection;
            }
            catch (Exception ex)
            {
                connection?.Dispose();
                throw new ConnectionException($"MySql connection failed. {ex.Message}", ex);
            }
        }

        public override async Task CreateDatabase(string databaseName, CancellationToken cancellationToken)
        {
            try
            {
                DefaultDatabase = "";

                using (var connection = await NewConnection())
                using (var cmd = CreateCommand(connection, "create database " + AddDelimiter(databaseName)))
                {
                    var value = await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                DefaultDatabase = databaseName;
            }
            catch (Exception ex)
            {
                throw new ConnectionException($"Create database {databaseName} failed. {ex.Message}", ex);
            }
        }

        public override async Task<List<string>> GetDatabaseList(CancellationToken cancellationToken)
        {
            try
            {
                var list = new List<string>();

                using (var connection = await NewConnection())
                using (var cmd = CreateCommand(connection, "SHOW DATABASES"))
                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        list.Add((string)reader["Database"]);
                    }
                }
                return list;
            }
            catch (Exception ex)
            {
                throw new ConnectionException($"Get database list failed. {ex.Message}", ex);
            }
        }

        public override async Task<List<Table>> GetTableList(CancellationToken cancellationToken)
        {
            try
            {
                var tableList = new List<Table>();

                using (var connection = await NewConnection())
                {

                    using (var cmd = CreateCommand(connection, "SHOW TABLES"))
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
                    {
                        while (await reader.ReadAsync(cancellationToken))
                        {
							var table = new Table
							{
								Name = reader[0].ToString()
							};
							tableList.Add(table);
                        }
                    }

                }
                return tableList;
            }
            catch (Exception ex)
            {
                throw new ConnectionException($"Get table list failed. {ex.Message}", ex);
            }
        }

        public override async Task<Table> GetSourceTableInfo(Table originalTable, CancellationToken cancellationToken)
        {
            if (originalTable.UseQuery)
            {
                return await GetQueryTable(originalTable, cancellationToken);
            }
            
            try
            {
				var schema = string.IsNullOrEmpty(originalTable.Schema) ? "public" : originalTable.Schema;
                var table = new Table(originalTable.Name, originalTable.Schema);

                using (var connection = await NewConnection())
                {

                    //The new datatable that will contain the table schema
                    table.Columns.Clear();

                    // The schema table 
                    using (var cmd = CreateCommand(connection, @"SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '" + DefaultDatabase + "' AND TABLE_NAME='" + table.Name + "'"))
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
                    {
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            var isSigned = reader["COLUMN_TYPE"].ToString().IndexOf("unsigned", StringComparison.Ordinal) > 0;
                            var col = new TableColumn
                            {
                                Name = reader["COLUMN_NAME"].ToString(),
                                LogicalName = reader["COLUMN_NAME"].ToString(),
                                IsInput = false,
                                DataType = ConvertSqlToTypeCode(reader["DATA_TYPE"].ToString(), isSigned),
                                AllowDbNull = reader["IS_NULLABLE"].ToString() != "NO" 
                            };

                            if (reader["COLUMN_KEY"].ToString() == "PRI")
                            {
                                col.DeltaType = TableColumn.EDeltaType.NaturalKey;
                            }
                            else if (col.DataType == ETypeCode.Unknown)
                            {
                                col.DeltaType = TableColumn.EDeltaType.IgnoreField;
                            }
                            else
                            {
                                col.DeltaType = TableColumn.EDeltaType.TrackingField;
                            }

							switch (col.DataType)
							{
							    case ETypeCode.String:
							        col.MaxLength = ConvertNullableToInt(reader["CHARACTER_MAXIMUM_LENGTH"]);
							        break;
							    case ETypeCode.Double:
							    case ETypeCode.Decimal:
							        col.Precision = ConvertNullableToInt(reader["NUMERIC_PRECISION"]);
							        col.Scale = ConvertNullableToInt(reader["NUMERIC_SCALE"]);
							        break;
							}

                            //make anything with a large string unlimited.  This will be created as varchar(max)
                            if (col.MaxLength > 4000)
                                col.MaxLength = null;

                            col.Description = reader["COLUMN_COMMENT"].ToString();
                            //col.IsUnique = Convert.ToBoolean(reader["IsUnique"]);


                            table.Columns.Add(col);
                        }
                    }
                }
                return table;
            }
            catch (Exception ex)
            {
                throw new ConnectionException($"Get source table information for {originalTable.Name} failed. {ex.Message}", ex);
            }
        }

		private int? ConvertNullableToInt(object value)
		{
			if(value == null || value is DBNull)
			{
				return null;
			}

		    var parsed = int.TryParse(value.ToString(), out var result);
		    if(parsed) 
		    {
		        return result;
		    }

		    return null;
		}


        public ETypeCode ConvertSqlToTypeCode(string sqlType, bool isSigned)
        {
            switch (sqlType)
            {
				case "bit": 
				    return ETypeCode.Boolean;
				case "tinyint": 
				    return isSigned ? ETypeCode.SByte : ETypeCode.Byte;
				case "year": 
				    return  ETypeCode.Int16;				       
				case "smallint": 
                    return isSigned ? ETypeCode.Int16 : ETypeCode.UInt16;
                case "mediumint":
				case "int": 
                    return isSigned ? ETypeCode.Int32 : ETypeCode.UInt32;
				case "bigint": 
                    return isSigned ? ETypeCode.Int64 : ETypeCode.UInt64;
				case "numeric": 
                case "decimal": 
                    return ETypeCode.Decimal;
				case "float":
                case "real":
                case "double precicion":
				    return ETypeCode.Double;
				case "bool": 
				case "boolean": 
				    return ETypeCode.Boolean;
				case "date":
				case "datetime":
				case "timestamp":
				    return ETypeCode.DateTime;
				case "time": 
				    return ETypeCode.Time;
				case "char": 
				case "varchar": 
				case "enum": 
				case "set": 
				case "tinytext": 
				case "mediumtext": 
				case "longtext": 
				    return ETypeCode.String;
				case "text":
					return ETypeCode.Text;
                case "binary": 
                case "varbinary": 
                case "tinyblob": 
                case "blob": 
                case "mediumblob": 
                case "longblob":
                    return ETypeCode.Binary;
            }
            return ETypeCode.Unknown;
        }

        public override async Task TruncateTable(Table table, CancellationToken cancellationToken)
        {
            try
            {
                using (var connection = await NewConnection())
                using (var cmd = connection.CreateCommand())
                {

                    cmd.CommandText = "truncate table " + AddDelimiter(table.Name);

                    try
                    {
                        await cmd.ExecuteNonQueryAsync(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    catch (Exception)
                    {
                        cmd.CommandText = "delete from " + AddDelimiter(table.Name);
                        await cmd.ExecuteNonQueryAsync(cancellationToken);
                    }
                }
            }
            catch(Exception ex)
            {
                throw new ConnectionException($"Truncate table {table.Name} failed. {ex.Message}", ex);

            }
        }

        public override async Task<long> ExecuteInsert(Table table, List<InsertQuery> queries, CancellationToken cancellationToken)
        {
            try
            {
                var autoIncrementSql = "";
                var deltaColumn = table.GetDeltaColumn(TableColumn.EDeltaType.AutoIncrement);
                if (deltaColumn != null)
                {
                    autoIncrementSql = "SELECT max(" + AddDelimiter(deltaColumn.Name) + ") from " + AddDelimiter(table.Name);
                }

                long identityValue = 0;

                using (var connection = await NewConnection())
                {
                    var insert = new StringBuilder();
                    var values = new StringBuilder();

                    using (var transaction = connection.BeginTransaction())
                    {
                        foreach (var query in queries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            insert.Clear();
                            values.Clear();

                            insert.Append("INSERT INTO " + AddDelimiter(table.Name) + " (");
                            values.Append("VALUES (");

                            for (var i = 0; i < query.InsertColumns.Count; i++)
                            {
                                insert.Append(AddDelimiter(query.InsertColumns[i].Column.Name) + ",");
                                values.Append("@col" + i + ",");
                            }

                            var insertCommand = insert.Remove(insert.Length - 1, 1) + ") " +
                                values.Remove(values.Length - 1, 1) + "); " + autoIncrementSql;

                            try
                            {
                                using (var cmd = connection.CreateCommand())
                                {
                                    cmd.CommandText = insertCommand;
                                    cmd.Transaction = transaction;

                                    for (var i = 0; i < query.InsertColumns.Count; i++)
                                    {
                                        var param = cmd.CreateParameter();
                                        param.ParameterName = "@col" + i;
                                        param.Value = query.InsertColumns[i].Value == null ? DBNull.Value : query.InsertColumns[i].Value;
                                        cmd.Parameters.Add(param);
                                    }

                                    var identity = await cmd.ExecuteScalarAsync(cancellationToken);
                                    identityValue = Convert.ToInt64(identity);

                                }
                            }
                            catch (Exception ex)
                            {
                                throw new ConnectionException($"The insert query failed.  {ex.Message}");
                            }
                        }
                        transaction.Commit();
                    }

					return identityValue;
                }
            }
            catch(Exception ex)
            {
                throw new ConnectionException($"Insert into table {table.Name} failed. {ex.Message}", ex);
            }
        }

        public override async Task ExecuteUpdate(Table table, List<UpdateQuery> queries, CancellationToken cancellationToken)
        {
            try
            {

                using (var connection = await NewConnection())
                {

                    var sql = new StringBuilder();

                    var rows = 0;

                    using (var transaction = connection.BeginTransaction())
                    {
                        foreach (var query in queries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            sql.Clear();
                            sql.Append("update " + AddDelimiter(table.Name) + " set ");

                            var count = 0;
                            foreach (var column in query.UpdateColumns)
                            {
                                sql.Append(AddDelimiter(column.Column.Name) + " = @col" + count + ",");
                                count++;
                            }
                            sql.Remove(sql.Length - 1, 1); //remove last comma
                            sql.Append(" " + BuildFiltersString(query.Filters) + ";");

                            //  Retrieving schema for columns from a single table
                            using (var cmd = connection.CreateCommand())
                            {
                                cmd.Transaction = transaction;
                                cmd.CommandText = sql.ToString();

                                var parameters = new MySqlParameter[query.UpdateColumns.Count];
                                for (var i = 0; i < query.UpdateColumns.Count; i++)
                                {
                                    var param = new MySqlParameter
                                    {
                                        ParameterName = "@col" + i,
                                        Value = query.UpdateColumns[i].Value == null
                                            ? DBNull.Value
                                            : query.UpdateColumns[i].Value
                                    };
                                    // param.MySqlDbType = GetSqlDbType(query.UpdateColumns[i].Column.Datatype);
                                    // param.Size = -1;
                                    cmd.Parameters.Add(param);
                                    parameters[i] = param;
                                }

                                try
                                {
                                    rows += await cmd.ExecuteNonQueryAsync(cancellationToken);
                                }
                                catch (Exception ex)
                                {
                                    throw new ConnectionException($"The update query failed. {ex.Message}");
                                }
                            }
                        }
                        transaction.Commit();
                    }
                }
            }
            catch (Exception ex)
            {
                throw new ConnectionException($"Update table {table.Name} failed. {ex.Message}", ex);
            }

        }
    }
}
