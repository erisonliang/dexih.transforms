﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CsvHelper;
using dexih.functions.Query;
using Dexih.Utils.DataType;

namespace dexih.functions.File
{
    public class FileHandlerText : FileHandlerBase, IDisposable
    {
        private Table _table;
        private ICollection<Filter> _filters;
        private readonly FileConfiguration _fileConfiguration;
        
        private CsvReader _csvReader;

        private readonly int _fileRowNumberOrdinal;

        private Dictionary<int, (int position, Type dataType)> _csvOrdinalMappings;

        private int _currentFileRowNumber;

        
        public FileHandlerText(Table table, FileConfiguration fileConfiguration)
        {
            _table = table;
            _fileConfiguration = fileConfiguration;
            
            _fileRowNumberOrdinal = table.GetDeltaColumnOrdinal(TableColumn.EDeltaType.FileRowNumber);
           
        }
        public override Task<ICollection<TableColumn>> GetSourceColumns(Stream data)
        {
            string[] headers;
            if (_fileConfiguration.HasHeaderRecord)
            {
                try
                {
                    using (var csv = new CsvReader(new StreamReader(data), _fileConfiguration))
                    {
                        csv.ReadHeader();
                        headers = csv.FieldHeaders;
                    }
                }
                catch (Exception ex)
                {
                    throw new FileHandlerException($"Error occurred opening the filestream: {ex.Message}", ex);
                }
            }
            else
            {
                // if no header row specified, then just create a series column names "column001, column002 ..."
                using (var csv = new CsvReader(new StreamReader(data), _fileConfiguration))
                {
                    csv.Read();
                    headers = Enumerable.Range(0, csv.CurrentRecord.Count()).Select(c => "column-" + c.ToString().PadLeft(3, '0')).ToArray();
                }
            }

            var columns = new List<TableColumn>();

            foreach (var field in headers)
            {
                var col = new TableColumn()
                {

                    //add the basic properties
                    Name = field,
                    LogicalName = field,
                    IsInput = false,
                    Datatype = DataType.ETypeCode.String,
                    DeltaType = TableColumn.EDeltaType.TrackingField,
                    Description = "",
                    AllowDbNull = true,
                    IsUnique = false
                };
                columns.Add(col);
            }

            columns.Add(new TableColumn()
            {

                //add the basic properties
                Name = "FileRow",
                LogicalName = "FileRow",
                IsInput = false,
                Datatype = DataType.ETypeCode.Int32,
                DeltaType = TableColumn.EDeltaType.FileRowNumber,
                Description = "The file row number the record came from.",
                AllowDbNull = false,
                IsUnique = false
            });

            return Task.FromResult((ICollection<TableColumn>) columns);
        }

        public override Task SetStream(Stream stream, ICollection<Filter> filters)
        {
            _currentFileRowNumber = 0;
            _filters = filters;
            var streamReder = new StreamReader(stream);
            
            if (_fileConfiguration != null)
            {
                _csvReader = new CsvReader(streamReder, _fileConfiguration);
            } 
            else 
            {
                _csvReader = new CsvReader(streamReder);
            }
            
            _csvOrdinalMappings = new Dictionary<int, (int position, Type dataType)>();

            // create mappings from column name positions, to the csv field name positions.
            if(_fileConfiguration != null && ( _fileConfiguration.MatchHeaderRecord || !_fileConfiguration.HasHeaderRecord))
            {
                _csvReader.ReadHeader();

                for(var col = 0; col < _table.Columns.Count; col++)
                {
                    var column = _table.Columns[col];
                    if (column.DeltaType != TableColumn.EDeltaType.FileName && column.DeltaType != TableColumn.EDeltaType.FileRowNumber)
                    {
                        for (var csvPos = 0; csvPos < _csvReader.FieldHeaders.Length; csvPos++)
                        {
                            if (_csvReader.FieldHeaders[csvPos] == column.Name)
                            {
                                _csvOrdinalMappings.Add(col, (csvPos, DataType.GetType(column.Datatype)));
                                break;
                            }
                        }
                    }
                }
            }
            else
            {
                // not matching in header, just create mappings equal
                for (var col = 0; col < _table.Columns.Count; col++)
                {
                    var column = _table.Columns[col];
                    if (column.DeltaType != TableColumn.EDeltaType.FileName && column.DeltaType != TableColumn.EDeltaType.FileRowNumber)
                    {
                        _csvOrdinalMappings.Add(col, (col, DataType.GetType(column.Datatype)));
                    }
                }
            }

            return Task.CompletedTask;

        }

        public override  Task<object[]> GetRow(object[] baseRow)
        {
            while (_csvReader != null && _csvReader.Read())
            {
                var row = new object[baseRow.Length];
                Array.Copy(baseRow, row, baseRow.Length);

                _currentFileRowNumber++;

                foreach (var colPos in _csvOrdinalMappings.Keys)
                {
                    var mapping = _csvOrdinalMappings[colPos];
                    row[colPos] = _csvReader.GetField(mapping.dataType, mapping.position);
                }

                if (_fileRowNumberOrdinal >= 0)
                {
                    row[_fileRowNumberOrdinal] = _currentFileRowNumber;
                }

                if (EvaluateRowFilter(row, _filters, _table))
                {
                    return Task.FromResult(row);
                }
            }
            return Task.FromResult((object[])null);
        }

        public override void Dispose()
        {
            _csvReader?.Dispose();
        }
    }
}