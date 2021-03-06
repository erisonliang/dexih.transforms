﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using dexih.functions;
using System.Threading;
using dexih.transforms.Exceptions;
using dexih.functions.Query;
using dexih.transforms.Transforms;

namespace dexih.transforms
{

    /// <summary>
    /// The join table is loaded into memory and then joined to the primary table.
    /// </summary>
    [Transform(
        Name = "Join",
        Description = "Join two tables by first loading the secondary table into memory.  This is fast when the secondary table is not large.",
        TransformType = TransformAttribute.ETransformType.Join
    )]
    public class TransformJoin : Transform
    {
        public TransformJoin() { }

        public TransformJoin(Transform primaryTransform, Transform joinTransform, List<JoinPair> joinPairs, List<TransformFunction> functions, EDuplicateStrategy joinDuplicateResolution, TableColumn joinSortField, string referenceTableAlias)
        {
            JoinPairs = joinPairs;
            Functions = functions;
            ReferenceTableAlias = referenceTableAlias;
            JoinDuplicateStrategy = joinDuplicateResolution;
            JoinSortField = joinSortField;

            SetInTransform(primaryTransform, joinTransform);
        }

        private bool _firstRead;
        private SortedDictionary<object[], List<object[]>> _joinHashData; //stores all the reference data grouped by the join keys (used for hashjoin).

        private JoinPair[] _joinColumns;
        
        private object[] _groupFields;
        private List<object[]> _groupData;
        private List<object[]> _filterdGroupData;
        private bool _writeGroup = false; //indicates a group is being written out
        private int _writeGroupPosition; //indicates the position in the group.
        private bool _joinReaderOpen;
        private bool _groupsOpen;
        private int[] _joinKeyOrdinals;
        private int[] _sourceKeyOrdinals;
        private string _referenceTableName;

        private JoinKeyComparer _joinKeyComparer;

        private readonly List<TransformFunction> _joinFilters = new List<TransformFunction>();


        public enum EJoinAlgorithm
        {
            Sorted, Hash
        }
        public EJoinAlgorithm JoinAlgorithm { get; protected set; }

        private int _primaryFieldCount;
        private int _referenceFieldCount;

        public override bool InitializeOutputFields()
        {
            if (ReferenceTransform == null )
                throw new Exception("There must a join table specified.");

            CacheTable = new Table("Join");

            var pos = 0;
            foreach(var column in PrimaryTransform.CacheTable.Columns)
            {
                CacheTable.Columns.Add(column.Copy());
                pos++;
            }
            foreach (var column in ReferenceTransform.CacheTable.Columns)
            {
                var newColumn = column.Copy();
                newColumn.ReferenceTable = ReferenceTableAlias;
                newColumn.IsIncrementalUpdate = false;

                // if (CacheTable.GetOrdinal(column.SchemaColumnName()) >= 0)
                // {
                //     throw new Exception("The join could not be initialized as the column " + column.SchemaColumnName() + " could not be found in the join table.");
                // }
                CacheTable.Columns.Add(newColumn);
                pos++;
            }

            var preFilters = new List<TransformFunction>();

            _referenceTableName = string.IsNullOrEmpty(ReferenceTransform.ReferenceTableAlias) ? ReferenceTransform.CacheTable.Name : ReferenceTransform.ReferenceTableAlias;

            List<FilterPair> filterJoins = null;
            if (JoinPairs != null)
            {
                _joinColumns = JoinPairs.Where(c => c.SourceColumn != null).ToArray();
                filterJoins = JoinPairs
                    .Where(c=>c.SourceColumn == null)
                    .Select(c => new FilterPair
                    {
                    Column1 = c.JoinColumn,
                    Column2 = null,
                    FilterValue = c.JoinValue,
                    Compare = Filter.ECompare.IsEqual
                }).ToList();
            }
            
            //seperate out the filers that only use the reference table and add them to prefilters from the ones required for joining.
            if (Functions != null)
            {
                foreach (var function in Functions)
                {
                    var isPrefilter = true;
                    foreach (var input in function.Inputs)
                    {
                        if (input.IsColumn)
                        {
                            if (input.Column.ReferenceTable != _referenceTableName)
                            {
                                isPrefilter = false;
                                break;
                            }
                        }

                    }
                    if (isPrefilter)
                        preFilters.Add(function);
                    else
                        _joinFilters.Add(function);
                }

            }

            if (preFilters.Count > 0 || filterJoins != null)
            {
                var preFilterTransform = new TransformFilter(ReferenceTransform, preFilters, filterJoins);
                ReferenceTransform = preFilterTransform;
            }


            //if the joinSortField has been, we need to enssure the reference dataset is sorted for duplication resolution.
            if(JoinSortField != null)
            {
                if(!SortFieldsMatch(RequiredReferenceSortFields(), ReferenceTransform.SortFields))
                {
                    var sortTransform = new TransformSort(ReferenceTransform, RequiredReferenceSortFields());
                    ReferenceTransform = sortTransform;
                }
            }

            _firstRead = true;

            _primaryFieldCount = PrimaryTransform.FieldCount;
            _referenceFieldCount = ReferenceTransform.FieldCount;
            _joinKeyComparer = new JoinKeyComparer();

            CacheTable.OutputSortFields = PrimaryTransform.CacheTable.OutputSortFields;

            return true;
        }

        public override bool RequiresSort => false;
        public override bool PassThroughColumns => true;


        public override async Task<bool> Open(Int64 auditKey, SelectQuery query, CancellationToken cancellationToken)
        {
            AuditKey = auditKey;
            if (query == null)
                query = new SelectQuery();

            //only apply a sort if there is not already a sort applied.
            // if(query.Sorts == null || query.Sorts.Count == 0)
                query.Sorts = RequiredSortFields();

            var returnValue = await PrimaryTransform.Open(auditKey, query, cancellationToken);
            if (!returnValue)
            {
                return false;
            }

            var referenceQuery = new SelectQuery()
            {
                Sorts = RequiredReferenceSortFields()
            };

            returnValue = await ReferenceTransform.Open(auditKey, referenceQuery, cancellationToken);
            if (!returnValue)
            {
                return false;
            }

            //check if the primary and reference transform are sorted in the join
            if (SortFieldsMatch(RequiredSortFields(), PrimaryTransform.SortFields) && SortFieldsMatch(RequiredReferenceSortFields(), ReferenceTransform.SortFields))
                JoinAlgorithm = EJoinAlgorithm.Sorted;
            else
                JoinAlgorithm = EJoinAlgorithm.Hash;

            //store the ordinals for the joins to improve performance.
            if (_joinColumns == null)
            {
                _joinKeyOrdinals = new int[0];
                _sourceKeyOrdinals = new int[0];
            }
            else
            {
                _joinKeyOrdinals = new int[_joinColumns.Length];
                _sourceKeyOrdinals = new int[_joinColumns.Length];

                for (var i = 0; i <  _joinColumns.Length; i++)
                {
                    _joinKeyOrdinals[i] = ReferenceTransform.GetOrdinal(_joinColumns[i].JoinColumn.Name);
                    _sourceKeyOrdinals[i] = _joinColumns[i].SourceColumn == null ? -1 : PrimaryTransform.GetOrdinal(_joinColumns[i].SourceColumn.Name);
                }
            }
            

            return true;
        }

        protected override async Task<object[]> ReadRecord(CancellationToken cancellationToken)
        {
            object[] newRow = null;
            var pos = 0;

            //this writes out duplicates of the primary reader when a duplicate match occurrs on the join table
            //i.e. outer join.
            if (_writeGroup)
            {
                //create a new row and write the primary fields out
                newRow = new object[FieldCount];
                for (var i = 0; i < _primaryFieldCount; i++)
                {
                    newRow[pos] = PrimaryTransform[i];
                    pos++;
                }

                var joinRow = _filterdGroupData[_writeGroupPosition];

                for (var i = 0; i < _referenceFieldCount; i++)
                {
                    newRow[pos] = joinRow[i];
                    pos++;
                }

                _writeGroupPosition++;

                //if last join record, then set the flag=false so the next read will read another primary row record.
                if (_writeGroupPosition >= _filterdGroupData.Count)
                    _writeGroup = false;

                return newRow;
            }

            //read a new row from the primary table.
            if (await PrimaryTransform.ReadAsync(cancellationToken)== false)
            {
                return null;
            }

            var joinMatchFound = false;

            //if input is sorted, then run a sortedjoin
            if (JoinAlgorithm == EJoinAlgorithm.Sorted)
            {
                //first read get a row from the join table.
                if (_firstRead)
                {
                    //get the first two rows from the join table.
                    _joinReaderOpen = await ReferenceTransform.ReadAsync(cancellationToken);
                    _groupsOpen = await ReadNextGroup();
                    _firstRead = false;
                }

                //loop through join table until we find a matching row.
                if (_joinColumns != null)
                {
                    while (_groupsOpen)
                    {
                        var joinFields = new object[_joinColumns.Length];
                        for (var i = 0; i < _joinColumns.Length; i++)
                        {
                            joinFields[i] = _joinColumns[i].SourceColumn == null ? _joinColumns[i].JoinValue : PrimaryTransform[_sourceKeyOrdinals[i]];
                        }

                        var compare = _joinKeyComparer.Compare(_groupFields, joinFields);
                        var done = false;
                        
                        switch (compare)
                        {
                            case 1:
                                joinMatchFound = false;
                                done = true;
                                break;
                            case -1:
                                if (_groupsOpen)
                                {
                                    _groupsOpen = await ReadNextGroup();
                                }

                                break;
                            case 0:
                                joinMatchFound = true;
                                done = true;
                                break;
                        }

                        if (done)
                        {
                            break;
                        }
                    }
                }
            }
            else //if input is not sorted, then run a hash join.
            {
                //first read load the join table into memory
                if (_firstRead)
                {
                    _joinHashData = new SortedDictionary<object[], List<object[]>>(new JoinKeyComparer());
                    _joinReaderOpen = await ReferenceTransform.ReadAsync(cancellationToken);
                    _groupsOpen = await ReadNextGroup();

                    //load all the join data into an a dictionary
                    while (_groupsOpen)
                    {
                        _joinHashData.Add(_groupFields, _groupData);
                        _groupsOpen = await ReadNextGroup();
                    }

                    _firstRead = false;
                }

                object[] sourceKeys;

                //set the values for the lookup
                if (_joinColumns != null)
                {
                    sourceKeys = new object[_joinColumns.Length];
                    for (var i = 0; i < _joinColumns.Length; i++)
                    {
                        sourceKeys[i] = _joinColumns[i].SourceColumn == null ? _joinColumns[i].JoinValue : PrimaryTransform[_sourceKeyOrdinals[i]];
                    }
                }
                else
                    sourceKeys = new object[0];

                if (_joinHashData.Keys.Contains(sourceKeys))
                {
                    _groupData = _joinHashData[sourceKeys];
                    _groupsOpen = true;
                    joinMatchFound = true;
                }
                else
                {
                    joinMatchFound = false;
                }
            }

            //create a new row and write the primary fields out
            newRow = new object[FieldCount];
            for (var i = 0; i < _primaryFieldCount; i++)
            {
                newRow[pos] = PrimaryTransform[i];
                pos++;
            }

            if (joinMatchFound)
            {
                //if there are additional join functions, we run them
                if (_joinFilters.Count == 0)
                {
                    _filterdGroupData = _groupData;
                }
                else {
                    _filterdGroupData = new List<object[]>();

                    //filter out the current group based on the functions defined.
                    foreach (var row in _groupData)
                    {
                        var matchFound = true;
                        foreach (var condition in _joinFilters)
                        {
                            foreach (var input in condition.Inputs.Where(c => c.IsColumn))
                            {
                                object value = null;
                                try
                                {
                                    if (input.Column.ReferenceTable == _referenceTableName)
                                    {
                                        value = row[ReferenceTransform.GetOrdinal(input.Column)];
                                    }
                                    else
                                    {
										value = PrimaryTransform[input.Column];
                                    }

                                    input.SetValue(value);

                                }
                                catch (Exception ex)
                                {
                                    throw new TransformException($"The join tansform {Name} failed setting parameters on the condition {condition.FunctionName} with the parameter {input.Name}.  {ex.Message}.", ex, value);
                                }
                            }

                            try
                            {
                                var invokeresult = condition.Invoke();

                                if ((bool)invokeresult == false)
                                {
                                    matchFound = false;
                                    break;
                                }
                            }
							catch (FunctionIgnoreRowException)
							{
								matchFound = false;
								TransformRowsIgnored++;
								break;
							}							
							catch (Exception ex)
                            {
                                throw new TransformException($"The join transform {Name} failed calling the function {condition.FunctionName}.  {ex.Message}.", ex);
                            }
                        }

                        if (matchFound)
                            _filterdGroupData.Add(row);
                    }
                }

                object[] joinRow = null;

                if (_filterdGroupData.Count > 0)
                {
                    if (_filterdGroupData.Count > 1)
                    {
                        switch (JoinDuplicateStrategy)
                        {
                            case EDuplicateStrategy.Abend:
                                throw new DuplicateJoinKeyException("The join transform failed as the selected columns on the join table " + ReferenceTableAlias + " are not unique.  To continue when duplicates occur set the join strategy to first, last or all.", ReferenceTableAlias, _groupFields);
                            case EDuplicateStrategy.First:
                                joinRow = _filterdGroupData[0];
                                break;
                            case EDuplicateStrategy.Last:
                                joinRow = _filterdGroupData.Last();
                                break;
                            case EDuplicateStrategy.All:
                                joinRow = _filterdGroupData[0];
                                _writeGroup = true;
                                _writeGroupPosition = 1;
                                break;
                            default:
                                throw new TransformException("The join transform failed due to an unknown join strategy "+ JoinDuplicateStrategy);
                        }
                    }
                    else
                        joinRow = _filterdGroupData[0];

                    for (var i = 0; i < _referenceFieldCount; i++)
                    {
                        newRow[pos] = joinRow[i];
                        pos++;
                    }
                }
            }

            return newRow;
        }

        /// <summary>
        /// Reads the next group of rows (based on join key) from the reference transform.
        /// </summary>
        /// <returns></returns>
        private async Task<bool> ReadNextGroup()
        {
            _groupData = new List<object[]>();

            if (_joinReaderOpen)
            {
                _groupData.Add(ReferenceTransform.CurrentRow);
            }

            while (_joinReaderOpen)
            {

                // if no joins, then the whole reference table is the group
                if (_joinColumns == null)
                {
                    _joinReaderOpen = await ReferenceTransform.ReadAsync();
                    _groupFields = new object[0];
                    if (!_joinReaderOpen)
                        break; 

                    _groupData.Add(ReferenceTransform.CurrentRow);
                }
                else
                {
                    _groupFields = new object[_joinColumns.Length];
                    for (var i = 0; i < _groupFields.Length; i++)
                    {
                        _groupFields[i] = ReferenceTransform[_joinKeyOrdinals[i]];
                    }

                    _joinReaderOpen = await ReferenceTransform.ReadAsync();
                    if (!_joinReaderOpen)
                    {
                        break;
                    }

                    var duplicateCheck = true;
                    for (var i = 0; i < _joinColumns.Length; i++)
                    {
                        if (!Equals(_groupFields[i], ReferenceTransform[_joinKeyOrdinals[i]]))
                        {
                            duplicateCheck = false;
                            break;
                        }
                    }

                    if (duplicateCheck)
                    {
                        _groupData.Add(ReferenceTransform.CurrentRow);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            return _groupData.Count > 0;
        }

        private class JoinKeyComparer : IComparer<object[]>
        {
            public int Compare(object[] x, object[] y)
            {
                for (var i = 0; i < x.Length; i++)
                {
                    var compareResult = ((IComparable)x[i]).CompareTo((IComparable)y[i]);

                    if (compareResult == 0)
                    {
                        continue;
                    }

                    return compareResult;
                }
                return 0;
            }
        }

        public override bool ResetTransform()
        {
            return true;
        }

        public override string Details()
        {
            return "Join";
        }

        public override List<Sort> RequiredSortFields()
        {
            var fields = new List<Sort>();
            if (_joinColumns != null)
            {
                foreach (var joinPair in _joinColumns)
                {
                    fields.Add(new Sort {Column = joinPair.SourceColumn, Direction = Sort.EDirection.Ascending});
                }
            }
            return fields;
        }

        public override List<Sort> RequiredReferenceSortFields()
        {
            var fields = new List<Sort>();
            if (_joinColumns != null)
            {
                foreach (var joinPair in _joinColumns)
                {
                    fields.Add(new Sort {Column = joinPair.JoinColumn, Direction = Sort.EDirection.Ascending});
                }
            }

            if (JoinSortField != null)
                fields.Add(new Sort(JoinSortField));

            return fields;
        }


    }


}
