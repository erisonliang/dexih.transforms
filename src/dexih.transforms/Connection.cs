﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using dexih.functions;
using System.Diagnostics;
using System.Data.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using dexih.functions.Query;
using static Dexih.Utils.DataType.DataType;
using dexih.transforms.Exceptions;
using dexih.transforms.Poco;

namespace dexih.transforms
{
    public abstract class Connection
    {

        #region Enums

        public enum EConnectionState
        {
            Broken = 0,
            Open = 1,
            Closed = 2,
            Fetching = 3,
            Connecting = 4,
            Executing = 5
        }

        [JsonConverter(typeof(StringEnumConverter))]
        public enum EConnectionCategory
        {
            SqlDatabase, // sql server, mysql, postgre etc.
            NoSqlDatabase, // Azure and others
            DatabaseFile, // coverts Excel, Sqlite where database is a simple file.
            File, // flat files
            WebService,
			Hub
        }

        #endregion

        #region Properties

        public string Name { get; set; }
        public virtual string Server { get; set; }
        public bool UseWindowsAuth { get; set; }
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string DefaultDatabase { get; set; }
        public string Filename { get; set; }
        public EConnectionState State { get; set; }

        public bool UseConnectionString { get; set; }
        public string ConnectionString { get; set; }

        #endregion

        #region Abstracts

        //Abstract Properties
        public abstract string ServerHelp { get; } //help text for what the server means for this description
        public abstract string DefaultDatabaseHelp { get; } //help text for what the default database means for this description

        public abstract string DatabaseTypeName { get; }
        public abstract EConnectionCategory DatabaseConnectionCategory { get; }
        public abstract bool AllowNtAuth { get; }
        public abstract bool AllowUserPass { get; }

        public abstract bool CanBulkLoad { get; }
        public abstract bool CanSort { get; }
        public abstract bool CanFilter { get; }
        public abstract bool CanUpdate { get; }
        public abstract bool CanDelete { get; }
        public abstract bool CanAggregate { get; }
        public abstract bool CanUseBinary { get; }
        public abstract bool CanUseSql { get; }
        public abstract bool DynamicTableCreation { get; } //connection allows any data columns to created dynamically (vs a preset table structure).

        public bool AllowAllPaths { get; set; } = true;
        public string[] AllowedPaths { get; set; } // list of paths the connection can use (flat file connections only).
        
        //Functions required for managed connection
        public abstract Task CreateTable(Table table, bool dropTable, CancellationToken cancellationToken);
        //public abstract Task TestConnection();
        public abstract Task ExecuteUpdate(Table table, List<UpdateQuery> queries, CancellationToken cancellationToken);
        public abstract Task ExecuteDelete(Table table, List<DeleteQuery> queries, CancellationToken cancellationToken);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="table"></param>
        /// <param name="queries"></param>
        /// <param name="cancellationToken"></param>
        /// <returns>The last autoincrement value</returns>
        public abstract Task<long> ExecuteInsert(Table table, List<InsertQuery> queries, CancellationToken cancellationToken);

        /// <summary>
        /// Runs a bulk insert operation for the connection.  
        /// </summary>
        /// <param name="table"></param>
        /// <param name="sourceData"></param>
        /// <param name="cancellationToken"></param>
        /// <returns>ReturnValue with the value = elapsed timer ticks taken to write the record.</returns>
        public abstract Task ExecuteInsertBulk(Table table, DbDataReader sourceData, CancellationToken cancellationToken);
        public abstract Task<object> ExecuteScalar(Table table, SelectQuery query, CancellationToken cancellationToken);
        public abstract Transform GetTransformReader(Table table, bool previewMode = false);
        public abstract Task TruncateTable(Table table, CancellationToken cancellationToken);
        public abstract Task<bool> TableExists(Table table, CancellationToken cancellationToken);

        /// <summary>
        /// If database connection supports direct DbDataReader.
        /// </summary>
        /// <param name="table"></param>
        /// <param name="connection"></param>
        /// <param name="query"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public abstract Task<DbDataReader> GetDatabaseReader(Table table, DbConnection connection, SelectQuery query, CancellationToken cancellationToken);

        //Functions required for datapoint.
        public abstract Task CreateDatabase(string databaseName, CancellationToken cancellationToken);
        public abstract Task<List<string>> GetDatabaseList(CancellationToken cancellationToken);
        public abstract Task<List<Table>> GetTableList(CancellationToken cancellationToken);

        /// <summary>
        /// Interrogates the underlying data to get the Table structure.
        /// </summary>
        /// <param name="table"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public abstract Task<Table> GetSourceTableInfo(Table table, CancellationToken cancellationToken);

        public async Task<Table> GetSourceTableInfo(string TableName, CancellationToken cancellationToken)
        {
            var table = new Table(TableName);
            var initResult = await InitializeTable(table, 0);
            if(initResult == null)
            {
                return null;
            }
            return await GetSourceTableInfo(initResult, cancellationToken);
        }

        /// <summary>
        /// Adds any database specific mandatory columns to the table object and returns the initialized version.
        /// </summary>
        /// <param name="table"></param>
        /// <param name="position"></param>
        /// <returns></returns>
        public abstract Task<Table> InitializeTable(Table table, int position);

        public Stopwatch WriteDataTimer = new Stopwatch();

        #endregion
        
        #region DataType ranges

        public virtual object GetConnectionMaxValue(ETypeCode typeCode, int length = 0)
        {
            return GetDataTypeMaxValue(typeCode, length);
        }

        public virtual object GetConnectionMinValue(ETypeCode typeCode)
        {
            return GetDataTypeMinValue(typeCode);
        }

        
        #endregion

        #region Audit

        /// <summary>
        /// Propulates the writerResult with a initial values, and writes the status to the database table.
        /// </summary>
        /// <param name="writreResult"></param>
        /// <param name="hubKey"></param>
        /// <param name="connectionKey"></param>
        /// <param name="auditType"></param>
        /// <param name="referenceKey"></param>
        /// <param name="parentAuditKey"></param>
        /// <param name="referenceName"></param>
        /// <param name="sourceTableKey"></param>
        /// <param name="sourceTableName"></param>
        /// <param name="targetTableKey"></param>
        /// <param name="targetTableName"></param>
        /// <param name="triggerMethod"></param>
        /// <param name="triggerInfo"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public virtual async Task InitializeAudit(TransformWriterResult writreResult, long hubKey, long connectionKey, string auditType, long referenceKey, long parentAuditKey, string referenceName, long sourceTableKey, string sourceTableName, long targetTableKey, string targetTableName, TransformWriterResult.ETriggerMethod triggerMethod, string triggerInfo, CancellationToken cancellationToken)
        {
            var picoTable = new PocoTable<TransformWriterResult>();

            TransformWriterResult previousResult = null;

            //create the audit table if it does not exist.
            var tableExistsResult = await picoTable.TableExists(this, cancellationToken);
            if (tableExistsResult == false)
            {
                //create the table if it doesn't already exist.
                await picoTable.CreateTable(this, false, cancellationToken);
            }
            else
            {
                //get the last audit result for this reference to collect previous run information
                previousResult = await GetPreviousResult(hubKey, connectionKey, referenceKey, CancellationToken.None);
            }

            writreResult.SetProperties(hubKey, connectionKey, 0, auditType, referenceKey, parentAuditKey, referenceName, sourceTableKey, sourceTableName, targetTableKey, targetTableName, this, previousResult, triggerMethod, triggerInfo);
            await picoTable.ExecuteInsert(this, writreResult, cancellationToken);
        }

        public virtual async Task UpdateAudit(TransformWriterResult writerResult, CancellationToken cancellationToken )
        {
            var picoTable = new PocoTable<TransformWriterResult>();

            writerResult.IsCurrent = true;
            writerResult.IsPrevious = false;
            writerResult.IsPreviousSuccess = false;

            //when the runstatuss is finished or finished with errors, set the previous success record to false.
            if (writerResult.RunStatus == TransformWriterResult.ERunStatus.Finished || writerResult.RunStatus == TransformWriterResult.ERunStatus.FinishedErrors)
            {
                var updateLatestColumn = new List<QueryColumn>() {
                    new QueryColumn(new TableColumn("IsCurrent", ETypeCode.Boolean), false),
                    new QueryColumn(new TableColumn("IsPreviousSuccess", ETypeCode.Boolean), false)
                };

                var updateLatestFilters = new List<Filter>() {
                    new Filter(new TableColumn("HubKey", ETypeCode.Int64), Filter.ECompare.IsEqual, writerResult.HubKey),
                    new Filter(new TableColumn("ReferenceKey", ETypeCode.Int64), Filter.ECompare.IsEqual, writerResult.ReferenceKey),
                    new Filter(new TableColumn("IsPreviousSuccess", ETypeCode.Boolean), Filter.ECompare.IsEqual, true),
                };

                var updateIsLatest = new UpdateQuery(picoTable.Table.Name, updateLatestColumn, updateLatestFilters);
                await ExecuteUpdate(picoTable.Table, new List<UpdateQuery>() { updateIsLatest }, CancellationToken.None);

                writerResult.IsPreviousSuccess = true;
            }

            //when finished, mark the previous result to false.
            if (writerResult.IsFinished)
            {
                var updateLatestColumn = new List<QueryColumn>() {
                    new QueryColumn(new TableColumn("IsCurrent", ETypeCode.Boolean), false),
                    new QueryColumn(new TableColumn("IsPrevious", ETypeCode.Boolean), false)
                };

                var updateLatestFilters = new List<Filter>() {
                    new Filter(new TableColumn("HubKey", ETypeCode.Int64), Filter.ECompare.IsEqual, writerResult.HubKey),
                    new Filter(new TableColumn("ReferenceKey", ETypeCode.Int64), Filter.ECompare.IsEqual, writerResult.ReferenceKey),
                    new Filter(new TableColumn("IsPrevious", ETypeCode.Boolean), Filter.ECompare.IsEqual, true),
                };

                var updateIsLatest = new UpdateQuery(picoTable.Table.Name, updateLatestColumn, updateLatestFilters);
                await ExecuteUpdate(picoTable.Table, new List<UpdateQuery>() { updateIsLatest }, CancellationToken.None);

                writerResult.IsCurrent = false;
                writerResult.IsPrevious = true;
            }

            await picoTable.ExecuteUpdate(this, writerResult, cancellationToken);

        }


        public virtual async Task<TransformWriterResult> GetPreviousResult(long hubKey, long connectionKey, long referenceKey, CancellationToken cancellationToken)
        {
            var results = await GetTransformWriterResults(hubKey, connectionKey, new long[] { referenceKey }, null, null, null, true, false, false, null, -1, null, false, cancellationToken);
            if (results == null || results.Count == 0)
            {
                return null;
            }
            return results[0];
        }

        public virtual async Task<TransformWriterResult> GetPreviousSuccessResult(long hubKey, long connectionKey, long referenceKey, CancellationToken cancellationToken)
        {
            var results = await GetTransformWriterResults(hubKey, connectionKey, new long[] { referenceKey }, null, null, null, false, true, false, null, -1, null, false, cancellationToken);
            if (results == null || results.Count == 0)
            {
                return null;
            }
            return results[0];
        }

        public virtual async Task<TransformWriterResult> GetCurrentResult(long hubKey, long connectionKey, long referenceKey, CancellationToken cancellationToken)
        {
            var results = await GetTransformWriterResults(hubKey, connectionKey, new long[] { referenceKey }, null, null, null, false, false, true, null, -1, null, false, cancellationToken);
            if (results == null || results.Count == 0)
            {
                return null;
            }
            return results[0];
        }

        public virtual async Task<List<TransformWriterResult>> GetPreviousResults(long hubKey, long connectionKey, long[] referenceKeys, CancellationToken cancellationToken)
        {
            return await GetTransformWriterResults(hubKey, connectionKey, referenceKeys, null, null, null, true, false, false, null, -1, null, false, cancellationToken);
        }

        public virtual async Task<List<TransformWriterResult>> GetPreviousSuccessResults(long hubKey, long connectionKey, long[] referenceKeys, CancellationToken cancellationToken)
        {
            return await GetTransformWriterResults(hubKey, connectionKey, referenceKeys, null, null, null, false, true, false, null, -1, null, false, cancellationToken);
        }

        public virtual async Task<List<TransformWriterResult>> GetCurrentResults(long hubKey, long connectionKey, long[] referenceKeys, CancellationToken cancellationToken)
        {
            return await GetTransformWriterResults(hubKey, connectionKey, referenceKeys, null, null, null, false, false, true, null, -1, null, false, cancellationToken);
        }

        public virtual async Task<List<TransformWriterResult>> GetTransformWriterResults(long? hubKey, long connectionKey, long[] referenceKeys, string auditType, long? auditKey, TransformWriterResult.ERunStatus? runStatus, bool previousResult, bool previousSuccessResult, bool currentResult, DateTime? startTime, int rows, long? parentAuditKey, bool childItems, CancellationToken cancellationToken)
        {
            Transform reader = null;
            var watch = new Stopwatch();
            watch.Start();

            var picoTable = new PocoTable<TransformWriterResult>();
            reader = GetTransformReader(picoTable.Table);

            var filters = new List<Filter>();
            if(hubKey != null) filters.Add(new Filter(new TableColumn("HubKey", ETypeCode.Int64), Filter.ECompare.IsEqual, hubKey));
            if (referenceKeys != null && referenceKeys.Length > 0) filters.Add(new Filter(new TableColumn("ReferenceKey", ETypeCode.Int64), Filter.ECompare.IsIn, referenceKeys));
            if (auditType != null) filters.Add(new Filter(new TableColumn("AuditType", ETypeCode.String), Filter.ECompare.IsEqual, auditType));
            if (auditKey != null) filters.Add(new Filter(new TableColumn("AuditKey", ETypeCode.Int64), Filter.ECompare.IsEqual, auditKey));
            if (runStatus != null) filters.Add(new Filter(new TableColumn("RunStatus", ETypeCode.String), Filter.ECompare.IsEqual, runStatus.ToString()));
            if (startTime != null) filters.Add(new Filter(new TableColumn("StartTime", ETypeCode.DateTime), Filter.ECompare.GreaterThanEqual, startTime));
            if (currentResult) filters.Add(new Filter(new TableColumn("IsCurrent", ETypeCode.Boolean), Filter.ECompare.IsEqual, true));
            if (previousResult) filters.Add(new Filter(new TableColumn("IsPrevious", ETypeCode.Boolean), Filter.ECompare.IsEqual, true));
            if (previousSuccessResult) filters.Add(new Filter(new TableColumn("IsPreviousSuccess", ETypeCode.Boolean), Filter.ECompare.IsEqual, true));
            if (parentAuditKey != null) filters.Add(new Filter(new TableColumn("ParentAuditKey", ETypeCode.Int64), Filter.ECompare.IsEqual, parentAuditKey));

            var sorts = new List<Sort>() { new Sort(new TableColumn("AuditKey", ETypeCode.Int64), Sort.EDirection.Descending) };
            var query = new SelectQuery() { Filters = filters, Sorts = sorts, Rows = rows };

            //add a sort transform to ensure sort order.
            reader = new TransformSort(reader, sorts);

            var returnValue = await reader.Open(0, query, cancellationToken);
            if (!returnValue)
            {
                throw new ConnectionException($"Failed to get the transform writer results on table {picoTable.Table} at {Name}.");
            }

            var pocoReader = new PocoLoader<TransformWriterResult>();
            var writerResults = await pocoReader.ToListAsync(reader, rows, cancellationToken);

            foreach(var result in writerResults)
            {
                result.AuditConnectionKey = connectionKey;
                
                if(childItems)
                {
                    result.ChildResults = await GetTransformWriterResults(hubKey, connectionKey, null, null, null, null, previousResult, previousSuccessResult, currentResult, null, 0, result.AuditKey, false, cancellationToken);
                }

                if (cancellationToken.IsCancellationRequested)
                    break;
            }

            watch.Stop();
            reader.Dispose();

            return writerResults;
        }

        #endregion

        public virtual bool IsValidDatabaseName(string name)
        {
            return true;
        }

        public virtual bool IsValidTableName(string name)
        {
            return true;
        }

        public virtual bool IsValidColumnName(string name)
        {
            return true;
        }


        /// <summary>
        /// Gets the next surrogatekey.
        /// </summary>
        /// <param name="table"></param>
        /// <param name="surrogateKeyColumn"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public virtual async Task<long> GetIncrementalKey(Table table, TableColumn surrogateKeyColumn, CancellationToken cancellationToken)
        {
            if(DynamicTableCreation)
            {
                return 0;
            }

            var query = new SelectQuery()
            {
                Columns = new List<SelectColumn>() { new SelectColumn(surrogateKeyColumn, SelectColumn.EAggregate.Max) },
                Table = table.Name
            };

            long surrogateKeyValue;
            var executeResult = await ExecuteScalar(table, query, cancellationToken);

            if (executeResult == null || executeResult is DBNull)
                surrogateKeyValue = 0;
            else
            {
                try
                {
                    var convertResult = TryParse(ETypeCode.Int64, executeResult);
                    surrogateKeyValue = (long)convertResult;
                } 
                catch(Exception ex)
                {
                    throw new ConnectionException($"Failed to get the surrogate key from {table.Name} on {Name} as the value is not a valid numeric.  {ex.Message}", ex);
                }
            }

            return surrogateKeyValue;

        }

        /// <summary>
        /// This is called to update any reference tables that need to store the surrogatekey, which is returned by the GetIncrementalKey.  
        /// For sql databases, this does not thing as as select max(key) is called to get key, however nosql tables have no max() function.
        /// </summary>
        /// <param name="table"></param>
        /// <param name="surrogateKeyColumn"></param>
        /// <param name="value"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public virtual Task UpdateIncrementalKey(Table table, string surrogateKeyColumn, long value, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Function runs when a data write comments.  This is used to put headers on csv files.
        /// </summary>
        /// <param name="table"></param>
        /// <returns></returns>
        public virtual Task DataWriterStart(Table table)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Function runs when a data write finishes.  This is used to close file streams.
        /// </summary>
        /// <param name="table"></param>
        /// <returns></returns>
        public virtual Task DataWriterFinish(Table table)
        {
            return Task.CompletedTask;
        }

        public async Task<Table> GetPreview(Table table, SelectQuery query, CancellationToken cancellationToken)
        {
            try
            {
                var watch = new Stopwatch();
                watch.Start();

                var rows = query?.Rows ?? -1;

                using (var reader = GetTransformReader(table, true))
                {
                    var returnValue = await reader.Open(0, query, cancellationToken);
                    if (!returnValue)
                    {
                        throw new ConnectionException($"The reader failed to open for table {table.Name} on {Name}");
                    }

                    reader.SetCacheMethod(Transform.ECacheMethod.OnDemandCache);
                    reader.SetEncryptionMethod(Transform.EEncryptionMethod.MaskSecureFields, "");

                    var count = 0;
                    while (
                        (count < rows || rows < 0) &&
                           cancellationToken.IsCancellationRequested == false &&
                           await reader.ReadAsync(cancellationToken)
                    )
                    {
                        count++;
                    }

                    watch.Stop();
                    return reader.CacheTable;
                }

            }
            catch (Exception ex)
            {
                throw new ConnectionException($"The preview failed to for table {table.Name} on {Name}", ex);
            }
        }


        /// <summary>
        /// This compares the physical table with the table structure to ensure that it can be used.
        /// </summary>
        /// <param name="table"></param>
        /// <param name="cancellationToken"></param>
        /// <returns>true if table matches, throw an exception is it does not match</returns>
        public virtual async Task<bool> CompareTable(Table table, CancellationToken cancellationToken)
        {
            var physicalTable = await GetSourceTableInfo(table, cancellationToken);
            if (physicalTable == null)
            {
                throw new ConnectionException($"The compare table failed to get the source table information for table {table.Name} at {Name}.");
            }

            foreach(var col in table.Columns)
            {
                var compareCol = physicalTable.Columns.SingleOrDefault(c => c.Name == col.Name);

                if (compareCol == null)
                {
                    throw new ConnectionException($"The source table {table.Name} does not contain the column {col.Name}.  Reimport the table or recreate the table with the missing column to fix.");
                }
            }

            return true;
        }

    }
}

