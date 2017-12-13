﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using dexih.functions.Query;
using Dexih.Utils.DataType;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace dexih.functions.File
{
    public class FileHandlerJson : FileHandlerBase
    {
        private readonly string _rowPath;
        private readonly Table _table;
        private IEnumerator<JToken> _jEnumerator;
        private int _responseDataOrdinal;
        private readonly Dictionary<string, (int Ordinal, DataType.ETypeCode Datatype)> _responseSegementOrdinals;

        public FileHandlerJson(Table table, string rowPath)
        {
            _rowPath = rowPath;
            _table = table;
            
            _responseDataOrdinal = _table.GetDeltaColumnOrdinal(TableColumn.EDeltaType.ResponseData);

            _responseSegementOrdinals = new Dictionary<string, (int ordinal, DataType.ETypeCode typeCode)>();
            
            foreach (var column in _table.Columns.Where(c => c.DeltaType == TableColumn.EDeltaType.ResponseSegment))
            {
                _responseSegementOrdinals.Add(column.Name, (_table.GetOrdinal(column.Name), column.Datatype));
            }
        }
        
        public override async Task<ICollection<TableColumn>> GetSourceColumns(Stream stream)
        {
            var reader = new StreamReader(stream);
            var jsonString = await reader.ReadToEndAsync();
            JToken content;
            try
            {
                content = JToken.Parse(jsonString);
            }
            catch (Exception ex)
            {
                throw new FileHandlerException($"Failed to parse the response json value. {ex.Message}", ex, stream);
            }

            var columns = new List<TableColumn>();

            if (content != null)
            {
                IEnumerable<JToken> tokens;
                if (string.IsNullOrEmpty(_rowPath))
                {
                    if (content.Type == JTokenType.Array)
                    {
                        tokens = content.First().Children();
                    }
                    else
                    {
                        tokens = content.Children();
                    }
                }
                else
                {
                    tokens = content.SelectTokens(_rowPath).First().Children();
                }
                
                foreach (var child in tokens)
                {

                    if (child.Type == JTokenType.Property)
                    {
                        var value = (JProperty)child;
                        DataType.ETypeCode dataType;
                        if (value.Value.Type == JTokenType.Array || value.Value.Type == JTokenType.Object || value.Value.Type == JTokenType.Property)
                        {
                            dataType = DataType.ETypeCode.Json;
                        }
                        else
                        {
                            dataType = DataType.GetTypeCode(value.Value.Type);
                        }
                        var col = new TableColumn
                        {
                            Name = value.Name,
                            IsInput = false,
                            LogicalName = value.Name,
                            Datatype = dataType,
                            DeltaType = TableColumn.EDeltaType.ResponseSegment,
                            MaxLength = null,
                            Description = "Json value of the " + value.Path + " path",
                            AllowDbNull = true,
                            IsUnique = false
                        };
                        columns.Add(col);
                    }
                    else
                    {
                        var col = new TableColumn
                        {
                            Name = child.Path,
                            IsInput = false,
                            LogicalName = child.Path,
                            Datatype = DataType.ETypeCode.Json,
                            DeltaType = TableColumn.EDeltaType.ResponseSegment,
                            MaxLength = null,
                            Description = "Json from the " + child.Path + " path",
                            AllowDbNull = true,
                            IsUnique = false
                        };
                        columns.Add(col);
                    }
                }
            }
            return columns;
        }

        public override async Task SetStream(Stream stream, ICollection<Filter> filters)
        {
            var reader = new StreamReader(stream);
            
            var jsonString = await reader.ReadToEndAsync();
            
            JToken jToken;

            try
            {
                jToken = JToken.Parse(jsonString);
                if (jToken == null)
                {
                    throw new FileHandlerException("The json data parsing returned nothing.");
                }
            }
            catch (Exception ex)
            {
                throw new FileHandlerException($"The json data could not be parsed.  {ex.Message}", ex);
            }
            
            if (string.IsNullOrEmpty(_rowPath))
            {
                if (jToken.Type == JTokenType.Array)
                {
                    _jEnumerator = jToken.Children().GetEnumerator();
                }
                else
                {
                    _jEnumerator = (new List<JToken>() {jToken}).GetEnumerator();
                }
            }
            else
            {
                _jEnumerator = jToken.SelectTokens(_rowPath).GetEnumerator();
            }
        }

        public override Task<object[]> GetRow(object[] baseRow)
        {
            if (_jEnumerator != null && _jEnumerator.MoveNext())
            {
                var row = new object[baseRow.Length];
                Array.Copy(baseRow, row, baseRow.Length);

                if (_responseDataOrdinal >= 0)
                {
                    row[_responseDataOrdinal] = _jEnumerator.Current.ToString();
                }

                foreach (var column in _responseSegementOrdinals)
                {
                    var value = _jEnumerator.Current.SelectToken(column.Key);
                        
                    try
                    {
                        row[column.Value.Ordinal] = DataType.TryParse(column.Value.Datatype, value);
                    }
                    catch (Exception ex)
                    {
                        throw new FileHandlerException(
                            $"Failed to convert value on column {column.Key} to datatype {column.Value.Datatype}. {ex.Message}",
                            ex, value);
                    }
                }

                return Task.FromResult(row);
            }
            else
            {
                return Task.FromResult((object[])null);
            }

        }

    }
}