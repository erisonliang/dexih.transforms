﻿using dexih.functions;

namespace dexih.transforms
{
    public class ColumnPair
    {
        public ColumnPair()
        {}
        
        /// <summary>
        /// Sets the source and target mappings to the same column name
        /// </summary>
        /// <param name="sourceTargetColumn">Column Name</param>
        public ColumnPair(TableColumn sourceTargetColumn)
        {
            SourceColumn = sourceTargetColumn;
            TargetColumn = sourceTargetColumn;
        }

        /// <summary>
        /// Sets the source and column mapping.
        /// </summary>
        /// <param name="sourceColumn">Source Column Name</param>
        /// <param name="targetColumn">Target Column Name</param>
        public ColumnPair(TableColumn sourceColumn, TableColumn targetColumn)
        {
            SourceColumn = sourceColumn;
            TargetColumn = targetColumn;
        }

        public TableColumn SourceColumn { get; set; }
        public TableColumn TargetColumn { get; set; }
    }

    /// <summary>
    /// Specifies joins to column or joins to static values.
    /// </summary>
    public class JoinPair
    {
        public JoinPair() { }
        public JoinPair(TableColumn sourceColumn, TableColumn joinColumn)
        {
            SourceColumn = sourceColumn;
            JoinColumn = joinColumn;
        }

        public JoinPair(TableColumn joinColumn, object joinValue)
        {
            JoinColumn = joinColumn;
            JoinValue = joinValue;
        }

        public TableColumn SourceColumn { get; set; }
        public TableColumn JoinColumn { get; set; }
        public object JoinValue { get; set; }
    }

    

   
}
