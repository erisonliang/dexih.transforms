﻿using System;
using System.Linq;
using System.Text;
using dexih.functions;
using static dexih.functions.DataType;
using System.Collections.Generic;

namespace dexih.transforms
{
    
    public class TableColumn
    {
        public TableColumn()
        {
            ExtendedProperties = new Dictionary<string, object>();
        }

        public TableColumn(string columName) :base()
        {
            ColumnName = columName;
            DataType = ETypeCode.String;
        }

        public TableColumn(string columName, ETypeCode dataType) : base()
        {
            ColumnName = columName;
            DataType = DataType;
        }

        public enum EDeltaType
        {
            SurrogateKey,
            SourceSurrogateKey,
            ValidFromDate,
            ValidToDate,
            CreateDate,
            UpdateDate,
            CreateAuditKey,
            UpdateAuditKey,
            IsCurrentField,
            NaturalKey,
            TrackingField,
            NonTrackingField,
            IgnoreField,
            ValidationStatus,
            RejectedReason,
            FileName,
            AutoGenerate,
            AzureRowKey, //special column type for Azure Storage Tables.  
            AzurePartitionKey,//special column type for Azure Storage Tables.  
        }

        public enum ESecurityFlag
        {
            None,
            Encrypt,
            OneWayHash
        }

        public string ColumnName { get; set; }
        public string LogicalName { get; set; }
        public string Description { get; set; }
        public ETypeCode DataType { get; set; }
        public int? MaxLength { get; set; }
        public int? Precision { get; set; }
        public int? Scale { get; set; }
        public bool AllowDbNull { get; set; }
        public EDeltaType DeltaType { get; set; }
        public bool IsUnique { get; set; }
        public bool IsMandatory { get; set; } = false;
        public ESecurityFlag SecurityFlag { get; set; } = ESecurityFlag.None;
        public bool IsInput { get; set; }
        public bool IsIncrementalUpdate { get; set; }
        public Dictionary<string, object> ExtendedProperties { get; set; }


        public Type ColumnGetType
        {
            get
            {
                return Type.GetType("System." + DataType);
            }
            set 
            {
                DataType = GetTypeCode(value);
            }
        }

        /// <summary>
        /// Is the column one form the source (vs. a value added column).
        /// </summary>
        /// <returns></returns>
        public bool IsSourceColumn()
        {
            switch (DeltaType)
            {
                case EDeltaType.NaturalKey:
                case EDeltaType.TrackingField:
                case EDeltaType.NonTrackingField:
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Columns which require no mapping and are generated automatically for auditing.
        /// </summary>
        /// <returns></returns>
        public bool IsGeneratedColumn()
        {
            switch (DeltaType)
            {
                case EDeltaType.CreateAuditKey:
                case EDeltaType.UpdateAuditKey:
                case EDeltaType.CreateDate:
                case EDeltaType.UpdateDate:
                case EDeltaType.SurrogateKey:
                case EDeltaType.AutoGenerate:
                case EDeltaType.ValidationStatus:
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Columns which indicate if the record is current.  These are the createdate, updatedate, iscurrentfield
        /// </summary>
        /// <returns></returns>
        public bool IsCurrentColumn()
        {
            switch (DeltaType)
            {
                case EDeltaType.ValidFromDate:
                case EDeltaType.ValidToDate:
                case EDeltaType.IsCurrentField:
                    return true;
            }
            return false;
        }

 
        /// <summary>
        /// Creates a copy of the column which can be used when generating other tables.
        /// </summary>
        /// <returns></returns>
        public TableColumn Copy()
        {
            return new TableColumn()
            {
                ColumnName = ColumnName,
                LogicalName = LogicalName,
                Description = Description,
                DataType = DataType,
                MaxLength = MaxLength,
                Precision = Precision,
                Scale = Scale,
                AllowDbNull = AllowDbNull,
                DeltaType = DeltaType,
                IsUnique = IsUnique,
                SecurityFlag = SecurityFlag,
                IsInput = IsInput,
                IsMandatory = IsMandatory,
                IsIncrementalUpdate = IsIncrementalUpdate
            };
        }
    }
}
