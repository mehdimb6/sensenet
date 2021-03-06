using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using SenseNet.ContentRepository.Storage.Schema;
using SenseNet.ContentRepository.Storage.Search.Internal;
using SenseNet.ContentRepository.Storage.Security;
using SenseNet.Diagnostics;
using Newtonsoft.Json;
using System.Data.Common;
using SenseNet.Configuration;

namespace SenseNet.ContentRepository.Storage.Data.SqlClient
{
    internal class BuiltInSqlProcedureFactory : IDataProcedureFactory
    {
        public IDataProcedure CreateProcedure()
        {
            return new SqlProcedure();
        }
    }

    internal class SqlProvider : DataProvider
    {
        //////////////////////////////////////// Internal Constants ////////////////////////////////////////

        internal const int StringPageSize = 80;
        internal const int StringDataTypeSize = 450;
        internal const int IntPageSize = 40;
        internal const int DateTimePageSize = 25;
        internal const int CurrencyPageSize = 15;
        internal const int TextAlternationSizeLimit = 4000; // (Autoloaded)NVarchar -> (Lazy)NText
        internal const int CsvParamSize = 8000;
        internal const int BinaryStreamBufferLength = 32768;
        internal const int IndexBlockSize = 100;

        internal const string StringMappingPrefix = "nvarchar_";
        internal const string DateTimeMappingPrefix = "datetime_";
        internal const string IntMappingPrefix = "int_";
        internal const string CurrencyMappingPrefix = "money_";

        private int _contentListStartPage;
        private Dictionary<DataType, int> _contentListMappingOffsets;


        public SqlProvider()
        {
            _contentListStartPage = 10000000;
            _contentListMappingOffsets = new Dictionary<DataType, int>();
            _contentListMappingOffsets.Add(DataType.String, StringPageSize * _contentListStartPage);
            _contentListMappingOffsets.Add(DataType.Int, IntPageSize * _contentListStartPage);
            _contentListMappingOffsets.Add(DataType.DateTime, DateTimePageSize * _contentListStartPage);
            _contentListMappingOffsets.Add(DataType.Currency, CurrencyPageSize * _contentListStartPage);
            _contentListMappingOffsets.Add(DataType.Binary, 0);
            _contentListMappingOffsets.Add(DataType.Reference, 0);
            _contentListMappingOffsets.Add(DataType.Text, 0);
        }


        public override int PathMaxLength
        {
            get { return StringDataTypeSize; }
        }
        public override DateTime DateTimeMinValue
        {
            get { return SqlDateTime.MinValue.Value; }
        }
        public override DateTime DateTimeMaxValue
        {
            get { return SqlDateTime.MaxValue.Value; }
        }
        public override decimal DecimalMinValue
        {
            get { return SqlMoney.MinValue.Value; }
        }
        public override decimal DecimalMaxValue
        {
            get { return SqlMoney.MaxValue.Value; }
        }

        public override ITransactionProvider CreateTransaction()
        {
            return new Transaction();
        }

        protected internal override INodeWriter CreateNodeWriter()
        {
            return new SqlNodeWriter();
        }

        protected internal override SchemaWriter CreateSchemaWriter()
        {
            return new SqlSchemaWriter();
        }

        //////////////////////////////////////// Initialization ////////////////////////////////////////

        protected override void InitializeForTestsPrivate()
        {
            using (var proc = CreateDataProcedure(@"
ALTER TABLE [BinaryProperties] CHECK CONSTRAINT ALL
ALTER TABLE [FlatProperties] CHECK CONSTRAINT ALL
ALTER TABLE [Nodes] CHECK CONSTRAINT ALL
ALTER TABLE [ReferenceProperties] CHECK CONSTRAINT ALL
ALTER TABLE [TextPropertiesNText] CHECK CONSTRAINT ALL
ALTER TABLE [TextPropertiesNVarchar] CHECK CONSTRAINT ALL
ALTER TABLE [Versions] CHECK CONSTRAINT ALL
"))
            {
                proc.CommandType = CommandType.Text;
                proc.ExecuteNonQuery();
            }
        }

        //////////////////////////////////////// Schema Members ////////////////////////////////////////

        protected internal override DataSet LoadSchema()
        {
            SqlConnection cn = new SqlConnection(ConnectionStrings.ConnectionString);
            SqlCommand cmd = new SqlCommand
            {
                Connection = cn,
                CommandTimeout = Configuration.Data.SqlCommandTimeout,
                CommandType = CommandType.StoredProcedure,
                CommandText = "proc_Schema_LoadAll"
            };
            SqlDataAdapter adapter = new SqlDataAdapter(cmd);
            DataSet dataSet = new DataSet();

            try
            {
                cn.Open();
                adapter.Fill(dataSet);
            }
            finally
            {
                cn.Close();
            }

            dataSet.Tables[0].TableName = "SchemaModification";
            dataSet.Tables[1].TableName = "DataTypes";
            dataSet.Tables[2].TableName = "PropertySetTypes";
            dataSet.Tables[3].TableName = "PropertySets";
            dataSet.Tables[4].TableName = "PropertyTypes";
            dataSet.Tables[5].TableName = "PropertySetsPropertyTypes";

            return dataSet;
        }

        protected internal override void Reset()
        {
            //TODO: Read the configuration if is exist
        }

        public override Dictionary<DataType, int> ContentListMappingOffsets
        {
            get { return _contentListMappingOffsets; }
        }

        protected internal override int ContentListStartPage
        {
            get { return _contentListStartPage; }
        }

        protected override PropertyMapping GetPropertyMappingInternal(PropertyType propType)
        {
            PropertyStorageSchema storageSchema = PropertyStorageSchema.SingleColumn;
            string tableName;
            string columnName;
            bool usePageIndex = false;
            int page = 0;

            switch (propType.DataType)
            {
                case DataType.String:
                    usePageIndex = true;
                    tableName = "FlatProperties";
                    columnName = SqlProvider.StringMappingPrefix + GetColumnIndex(propType.DataType, propType.Mapping, out page);
                    break;
                case DataType.Text:
                    usePageIndex = false;
                    tableName = "TextPropertiesNVarchar, TextPropertiesNText";
                    columnName = "Value";
                    storageSchema = PropertyStorageSchema.MultiTable;
                    break;
                case DataType.Int:
                    usePageIndex = true;
                    tableName = "FlatProperties";
                    columnName = SqlProvider.IntMappingPrefix + GetColumnIndex(propType.DataType, propType.Mapping, out page);
                    break;
                case DataType.Currency:
                    usePageIndex = true;
                    tableName = "FlatProperties";
                    columnName = SqlProvider.CurrencyMappingPrefix + GetColumnIndex(propType.DataType, propType.Mapping, out page);
                    break;
                case DataType.DateTime:
                    usePageIndex = true;
                    tableName = "FlatProperties";
                    columnName = SqlProvider.DateTimeMappingPrefix + GetColumnIndex(propType.DataType, propType.Mapping, out page);
                    break;
                case DataType.Binary:
                    usePageIndex = false;
                    tableName = "BinaryProperties";
                    columnName = "ContentType, FileNameWithoutExtension, Extension, Size, Stream";
                    storageSchema = PropertyStorageSchema.MultiColumn;
                    break;
                case DataType.Reference:
                    usePageIndex = false;
                    tableName = "ReferenceProperties";
                    columnName = "ReferredNodeId";
                    break;
                default:
                    throw new NotSupportedException("Unknown DataType" + propType.DataType);
            }
            return new PropertyMapping
            {
                StorageSchema = storageSchema,
                TableName = tableName,
                ColumnName = columnName,
                PageIndex = page,
                UsePageIndex = usePageIndex
            };
        }
        private static int GetColumnIndex(DataType dataType, int mapping, out int page)
        {
            int pageSize;
            switch (dataType)
            {
                case DataType.String: pageSize = SqlProvider.StringPageSize; break;
                case DataType.Int: pageSize = SqlProvider.IntPageSize; break;
                case DataType.DateTime: pageSize = SqlProvider.DateTimePageSize; break;
                case DataType.Currency: pageSize = SqlProvider.CurrencyPageSize; break;
                default:
                    page = 0;
                    return 0;
            }

            page = mapping / pageSize;
            int index = mapping % pageSize;
            return index + 1;
        }

        public override void AssertSchemaTimestampAndWriteModificationDate(long timestamp)
        {
            var script = @"DECLARE @Count INT
                            SELECT @Count = COUNT(*) FROM SchemaModification
                            IF @Count = 0
                                INSERT INTO SchemaModification (ModificationDate) VALUES (GETUTCDATE())
                            ELSE
                            BEGIN
                                UPDATE [SchemaModification] SET [ModificationDate] = GETUTCDATE() WHERE Timestamp = @Timestamp
                                IF @@ROWCOUNT = 0
                                    RAISERROR (N'Storage schema is out of date.', 12, 1);
                            END";

            using (var cmd = (SqlProcedure)DataProvider.CreateDataProcedure(script))
            {
                try
                {
                    cmd.CommandType = CommandType.Text;
                    cmd.Parameters.Add("@Timestamp", SqlDbType.Timestamp).Value = SqlProvider.GetBytesFromLong(timestamp);
                    cmd.ExecuteNonQuery();
                }
                catch (SqlException sex) // rethrow
                {
                    throw new DataException(sex.Message, sex);
                }
            }
        }

        protected internal override IEnumerable<int> QueryNodesByPath(string pathStart, bool orderByPath)
        {
            return QueryNodesByTypeAndPath(null, pathStart, orderByPath);
        }
        protected internal override IEnumerable<int> QueryNodesByType(int[] nodeTypeIds)
        {
            return QueryNodesByTypeAndPath(nodeTypeIds, new string[0], false);
        }
        protected internal override IEnumerable<int> QueryNodesByTypeAndPath(int[] nodeTypeIds, string pathStart, bool orderByPath)
        {
            return QueryNodesByTypeAndPathAndName(nodeTypeIds, new[] { pathStart }, orderByPath, null);
        }
        protected internal override IEnumerable<int> QueryNodesByTypeAndPath(int[] nodeTypeIds, string[] pathStart, bool orderByPath)
        {
            return QueryNodesByTypeAndPathAndName(nodeTypeIds, pathStart, orderByPath, null);
        }
        protected internal override IEnumerable<int> QueryNodesByTypeAndPathAndName(int[] nodeTypeIds, string pathStart, bool orderByPath, string name)
        {
            return QueryNodesByTypeAndPathAndName(nodeTypeIds, new[] { pathStart }, orderByPath, name);
        }
        protected internal override IEnumerable<int> QueryNodesByTypeAndPathAndName(int[] nodeTypeIds, string[] pathStart, bool orderByPath, string name)
        {
            var sql = new StringBuilder("SELECT NodeId FROM Nodes WHERE ");
            var first = true;

            if (pathStart != null && pathStart.Length > 0)
            {
                for (int i = 0; i < pathStart.Length; i++)
                    if (pathStart[i] != null)
                        pathStart[i] = pathStart[i].Replace("'", "''");

                sql.AppendLine("(");
                for (int i = 0; i < pathStart.Length; i++)
                {
                    if (i > 0)
                        sql.AppendLine().Append(" OR ");
                    sql.Append(" Path LIKE N'");
                    sql.Append(EscapeForLikeOperator(pathStart[i]));
                    if (!pathStart[i].EndsWith(RepositoryPath.PathSeparator))
                        sql.Append(RepositoryPath.PathSeparator);
                    sql.Append("%' COLLATE Latin1_General_CI_AS");
                }
                sql.AppendLine(")");
                first = false;
            }

            if (name != null)
            {
                name = name.Replace("'", "''");
                if (!first)
                    sql.Append(" AND");
                sql.Append(" Name = '").Append(name).Append("'");
                first = false;
            }

            if (nodeTypeIds != null)
            {
                if (!first)
                    sql.Append(" AND");
                sql.Append(" NodeTypeId");
                if (nodeTypeIds.Length == 1)
                    sql.Append(" = ").Append(nodeTypeIds[0]);
                else
                    sql.Append(" IN (").Append(String.Join(", ", nodeTypeIds)).Append(")");

                first = false;
            }

            if (orderByPath)
                sql.AppendLine().Append("ORDER BY Path");

            var result = new List<int>();
            using (var cmd = DataProvider.CreateDataProcedure(sql.ToString()))
            {
                cmd.CommandType = CommandType.Text;
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        result.Add(reader.GetSafeInt32(0));
                    return result;
                }
            }
        }
        protected internal override IEnumerable<int> QueryNodesByTypeAndPathAndProperty(int[] nodeTypeIds, string pathStart, bool orderByPath, List<QueryPropertyData> properties)
        {
            var sql = new StringBuilder("SELECT NodeId FROM SysSearchWithFlatsView WHERE ");
            var first = true;

            if (pathStart != null)
            {
                pathStart = pathStart.Replace("'", "''");
                sql.Append(" Path LIKE N'");
                sql.Append(EscapeForLikeOperator(pathStart));
                if (!pathStart.EndsWith(RepositoryPath.PathSeparator))
                    sql.Append(RepositoryPath.PathSeparator);
                sql.Append("%' COLLATE Latin1_General_CI_AS");
                first = false;
            }

            if (nodeTypeIds != null)
            {
                if (!first)
                    sql.Append(" AND");
                sql.Append(" NodeTypeId");
                if (nodeTypeIds.Length == 1)
                    sql.Append(" = ").Append(nodeTypeIds[0]);
                else
                    sql.Append(" IN (").Append(String.Join(", ", nodeTypeIds)).Append(")");

                first = false;
            }

            if (properties != null)
            {
                foreach (var queryPropVal in properties)
                {
                    if (string.IsNullOrEmpty(queryPropVal.PropertyName))
                        continue;

                    var pt = PropertyType.GetByName(queryPropVal.PropertyName);
                    var pm = pt == null ? null : pt.GetDatabaseInfo();
                    var colName = pm == null ? GetNodeAttributeName(queryPropVal.PropertyName) : pm.ColumnName;
                    var dt = pt == null ? GetNodeAttributeType(queryPropVal.PropertyName) : pt.DataType;

                    if (!first)
                        sql.Append(" AND");

                    if (queryPropVal.Value != null)
                    {
                        switch (dt)
                        {
                            case DataType.DateTime:
                            case DataType.String:
                                var stringValue = queryPropVal.Value.ToString().Replace("'", "''");
                                switch (queryPropVal.QueryOperator)
                                {
                                    case Operator.Equal:
                                        sql.AppendFormat(" {0} = '{1}'", colName, stringValue);
                                        break;
                                    case Operator.Contains:
                                        sql.AppendFormat(" {0} LIKE '%{1}%'", colName, EscapeForLikeOperator(stringValue));
                                        break;
                                    case Operator.StartsWith:
                                        sql.AppendFormat(" {0} LIKE '{1}%'", colName, EscapeForLikeOperator(stringValue));
                                        break;
                                    case Operator.EndsWith:
                                        sql.AppendFormat(" {0} LIKE '%{1}'", colName, EscapeForLikeOperator(stringValue));
                                        break;
                                    case Operator.GreaterThan:
                                        sql.AppendFormat(" {0} > '{1}'", colName, stringValue);
                                        break;
                                    case Operator.GreaterThanOrEqual:
                                        sql.AppendFormat(" {0} >= '{1}'", colName, stringValue);
                                        break;
                                    case Operator.LessThan:
                                        sql.AppendFormat(" {0} < '{1}'", colName, stringValue);
                                        break;
                                    case Operator.LessThanOrEqual:
                                        sql.AppendFormat(" {0} <= '{1}'", colName, stringValue);
                                        break;
                                    case Operator.NotEqual:
                                        sql.AppendFormat(" {0} <> '{1}'", colName, stringValue);
                                        break;
                                    default:
                                        throw new InvalidOperationException(string.Format("Direct query not supported (data type: {0}, operator: {1})", dt, queryPropVal.QueryOperator));
                                }
                                break;
                            case DataType.Int:
                            case DataType.Currency:
                                switch (queryPropVal.QueryOperator)
                                {
                                    case Operator.Equal:
                                        sql.AppendFormat(" {0} = {1}", colName, queryPropVal.Value);
                                        break;
                                    case Operator.GreaterThan:
                                        sql.AppendFormat(" {0} > {1}", colName, queryPropVal.Value);
                                        break;
                                    case Operator.GreaterThanOrEqual:
                                        sql.AppendFormat(" {0} >= {1}", colName, queryPropVal.Value);
                                        break;
                                    case Operator.LessThan:
                                        sql.AppendFormat(" {0} < {1}", colName, queryPropVal.Value);
                                        break;
                                    case Operator.LessThanOrEqual:
                                        sql.AppendFormat(" {0} <= {1}", colName, queryPropVal.Value);
                                        break;
                                    case Operator.NotEqual:
                                        sql.AppendFormat(" {0} <> {1}", colName, queryPropVal.Value);
                                        break;
                                    default:
                                        throw new InvalidOperationException(string.Format("Direct query not supported (data type: {0}, operator: {1})", dt, queryPropVal.QueryOperator));
                                }
                                break;
                            default:
                                throw new NotSupportedException("Not supported direct query dataType: " + dt);
                        }
                    }
                    else
                    {
                        sql.Append(" IS NULL");
                    }
                }
            }

            if (orderByPath)
                sql.AppendLine().Append("ORDER BY Path");

            var cmd = new SqlProcedure { CommandText = sql.ToString(), CommandType = CommandType.Text };
            SqlDataReader reader = null;
            var result = new List<int>();
            try
            {
                reader = cmd.ExecuteReader();
                while (reader.Read())
                    result.Add(reader.GetSafeInt32(0));
                return result;
            }
            finally
            {
                if (reader != null && !reader.IsClosed)
                    reader.Close();
                cmd.Dispose();
            }
        }
        protected internal override IEnumerable<int> QueryNodesByReferenceAndType(string referenceName, int referredNodeId, int[] allowedTypeIds)
        {
            if (referenceName == null)
                throw new ArgumentNullException("referenceName");
            if (referenceName.Length == 0)
                throw new ArgumentException("Argument referenceName cannot be empty.", "referenceName");
            var referenceProperty = ActiveSchema.PropertyTypes[referenceName];
            if (referenceProperty == null)
                throw new ArgumentException("PropertyType is not found: " + referenceName, "referenceName");
            var referencePropertyId = referenceProperty.Id;

            string sql;
            if (allowedTypeIds == null || allowedTypeIds.Length == 0)
            {
                sql = @"SELECT V.NodeId FROM ReferenceProperties R
	JOIN Versions V ON R.VersionId = V.VersionId
	JOIN Nodes N ON V.VersionId = N.LastMinorVersionId
WHERE R.PropertyTypeId = @PropertyTypeId AND R.ReferredNodeId = @ReferredNodeId";
            }
            else
            {
                sql = String.Format(@"SELECT N.NodeId FROM ReferenceProperties R
	JOIN Versions V ON R.VersionId = V.VersionId
	JOIN Nodes N ON V.VersionId = N.LastMinorVersionId
WHERE R.PropertyTypeId = @PropertyTypeId AND R.ReferredNodeId = @ReferredNodeId AND N.NodeTypeId IN ({0})", String.Join(", ", allowedTypeIds));
            }

            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                cmd.Parameters.Add("@PropertyTypeId", SqlDbType.Int).Value = referencePropertyId;
                cmd.Parameters.Add("@ReferredNodeId", SqlDbType.Int).Value = referredNodeId;
                var result = new List<int>();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        result.Add(reader.GetSafeInt32(0));
                    return result;
                }
            }
        }

        private static string GetNodeAttributeName(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName))
                throw new ArgumentNullException("propertyName");

            switch (propertyName)
            {
                case "Id":
                    return "NodeId";
                case "ParentId":
                case "Parent":
                    return "ParentNodeId";
                case "Locked":
                    return "Locked";
                case "LockedById":
                case "LockedBy":
                    return "LockedById";
                case "MajorVersion":
                    return "MajorNumber";
                case "MinorVersion":
                    return "MinorNumber";
                case "CreatedById":
                case "CreatedBy":
                    return "CreatedById";
                case "ModifiedById":
                case "ModifiedBy":
                    return "ModifiedById";
                default:
                    return propertyName;
            }
        }
        private static DataType GetNodeAttributeType(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName))
                throw new ArgumentNullException("propertyName");

            switch (propertyName)
            {
                case "Id":
                case "IsDeleted":
                case "IsInherited":
                case "ParentId":
                case "Parent":
                case "Index":
                case "Locked":
                case "LockedById":
                case "LockedBy":
                case "LockType":
                case "LockTimeout":
                case "MajorVersion":
                case "MinorVersion":
                case "CreatedById":
                case "CreatedBy":
                case "ModifiedById":
                case "ModifiedBy":
                case "IsSystem":
                case "OwnerId":
                case "SavingState":
                    return DataType.Int;
                case "Name":
                case "Path":
                case "ETag":
                case "LockToken":
                    return DataType.String;
                case "LockDate":
                case "LastLockUpdate":
                case "CreationDate":
                case "ModificationDate":
                    return DataType.DateTime;
                default:
                    return DataType.String;
            }
        }

        protected internal override int InstanceCount(int[] nodeTypeIds)
        {
            var sql = new StringBuilder("SELECT COUNT(*) FROM Nodes WHERE NodeTypeId");
            if (nodeTypeIds.Length == 1)
                sql.Append(" = ").Append(nodeTypeIds[0]);
            else
                sql.Append(" IN (").Append(String.Join(", ", nodeTypeIds)).Append(")");

            var cmd = new SqlProcedure { CommandText = sql.ToString(), CommandType = CommandType.Text }; ;
            try
            {
                var count = (int)cmd.ExecuteScalar();
                return count;
            }
            finally
            {
                cmd.Dispose();
            }

        }

        //////////////////////////////////////// Node Query ////////////////////////////////////////

        protected internal override VersionNumber[] GetVersionNumbers(int nodeId)
        {
            List<VersionNumber> versions = new List<VersionNumber>();
            SqlProcedure cmd = null;
            SqlDataReader reader = null;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_VersionNumbers_GetByNodeId" };
                cmd.Parameters.Add("@NodeId", SqlDbType.Int).Value = nodeId;
                reader = cmd.ExecuteReader();

                int majorNumberIndex = reader.GetOrdinal("MajorNumber");
                int minorNumberIndex = reader.GetOrdinal("MinorNumber");
                int statusIndex = reader.GetOrdinal("Status");

                while (reader.Read())
                {
                    versions.Add(new VersionNumber(
                        reader.GetInt16(majorNumberIndex),
                        reader.GetInt16(minorNumberIndex),
                        (VersionStatus)reader.GetInt16(statusIndex)));
                }
            }
            finally
            {
                if (reader != null && !reader.IsClosed)
                    reader.Close();

                cmd.Dispose();
            }
            return versions.ToArray();
        }

        protected internal override VersionNumber[] GetVersionNumbers(string path)
        {
            List<VersionNumber> versions = new List<VersionNumber>();
            SqlProcedure cmd = null;
            SqlDataReader reader = null;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_VersionNumbers_GetByPath" };
                cmd.Parameters.Add("@Path", SqlDbType.NVarChar, 450).Value = path;
                reader = cmd.ExecuteReader();

                int majorNumberIndex = reader.GetOrdinal("MajorNumber");
                int minorNumberIndex = reader.GetOrdinal("MinorNumber");
                int statusIndex = reader.GetOrdinal("Status");

                while (reader.Read())
                {
                    versions.Add(new VersionNumber(
                        reader.GetInt32(majorNumberIndex),
                        reader.GetInt32(minorNumberIndex),
                        (VersionStatus)reader.GetInt32(statusIndex)));
                }
            }
            finally
            {
                if (reader != null && !reader.IsClosed)
                    reader.Close();

                cmd.Dispose();
            }
            return versions.ToArray();
        }


        protected internal override void LoadNodes(Dictionary<int, NodeBuilder> buildersByVersionId)
        {
            var versionIds = buildersByVersionId.Keys.Take(21).ToArray();

            using (var op = SnTrace.Database.StartOperation("SqlProvider.LoadNodes: VersionIds: count:{0}, items:[{1}]", buildersByVersionId.Count, versionIds))
            {
                var builders = buildersByVersionId; // Shortcut
                SqlProcedure cmd = null;
                SqlDataReader reader = null;
                try
                {
                    cmd = new SqlProcedure { CommandText = "proc_Node_LoadData_Batch" };
                    string xmlIds = CreateIdXmlForNodeInfoBatchLoad(builders);
                    cmd.Parameters.Add("@IdsInXml", SqlDbType.Xml).Value = xmlIds;
                    reader = cmd.ExecuteReader();

                    // #1: FlatProperties
                    // SELECT * FROM FlatProperties
                    //    WHERE VersionId IN (select id from @versionids)
                    var versionIdIndex = reader.GetOrdinal("VersionId");
                    var pageIndex = reader.GetOrdinal("Page");

                    while (reader.Read())
                    {
                        int versionId = reader.GetInt32(versionIdIndex);
                        int page = reader.GetInt32(pageIndex);
                        NodeBuilder builder = builders[versionId];
                        foreach (PropertyType pt in builder.Token.AllPropertyTypes)
                        {
                            string mapping = PropertyMap.GetValidMapping(page, pt);
                            if (mapping.Length != 0)
                            {
                                // Mapped property appears in the given page
                                object val = reader[mapping];
                                if (val is DateTime)
                                {
                                    val = DateTime.SpecifyKind((DateTime)val, DateTimeKind.Utc);
                                }

                                builder.AddDynamicProperty(pt, (val == DBNull.Value) ? null : val);
                            }
                        }
                    }

                    reader.NextResult();


                    // #2: BinaryProperties
                    // SELECT B.BinaryPropertyId, B.VersionId, B.PropertyTypeId, F.FileId, F.ContentType, F.FileNameWithoutExtension,
                    //     F.Extension, F.[Size], F.[Checksum], NULL AS Stream, 0 AS Loaded, F.[Timestamp]
                    // FROM dbo.BinaryProperties B
                    //     JOIN dbo.Files F ON B.FileId = F.FileId
                    // WHERE PropertyTypeId IN (select id from @binids) AND VersionId IN (select id from @versionids)
                    var binaryPropertyIdIndex = reader.GetOrdinal("BinaryPropertyId");
                    versionIdIndex = reader.GetOrdinal("VersionId");
                    var checksumPropertyIndex = reader.GetOrdinal("Checksum");
                    var propertyTypeIdIndex = reader.GetOrdinal("PropertyTypeId");
                    var fileIdIndex = reader.GetOrdinal("FileId");
                    var contentTypeIndex = reader.GetOrdinal("ContentType");
                    var fileNameWithoutExtensionIndex = reader.GetOrdinal("FileNameWithoutExtension");
                    var extensionIndex = reader.GetOrdinal("Extension");
                    var sizeIndex = reader.GetOrdinal("Size");
                    var timestampIndex = reader.GetOrdinal("Timestamp");

                    while (reader.Read())
                    {
                        string ext = reader.GetString(extensionIndex);
                        if (ext.Length != 0)
                            ext = ext.Remove(0, 1); // Remove dot from the start if extension is not empty

                        string fn = reader.GetSafeString(fileNameWithoutExtensionIndex); // reader.IsDBNull(fileNameWithoutExtensionIndex) ? null : reader.GetString(fileNameWithoutExtensionIndex);

                        var x = new BinaryDataValue
                        {
                            Id = reader.GetInt32(binaryPropertyIdIndex),
                            FileId = reader.GetInt32(fileIdIndex),
                            Checksum = reader.GetSafeString(checksumPropertyIndex),
                            FileName = new BinaryFileName(fn, ext),
                            ContentType = reader.GetString(contentTypeIndex),
                            Size = reader.GetInt64(sizeIndex),
                            Timestamp = DataProvider.GetLongFromBytes((byte[])reader.GetValue(timestampIndex))
                        };

                        var versionId = reader.GetInt32(versionIdIndex);
                        var propertyTypeId = reader.GetInt32(propertyTypeIdIndex);
                        builders[versionId].AddDynamicProperty(propertyTypeId, x);
                    }

                    reader.NextResult();


                    // #3: ReferencePropertyInfo + Referred NodeToken
                    // SELECT VersionId, PropertyTypeId, ReferredNodeId
                    // FROM dbo.ReferenceProperties ref
                    // WHERE ref.VersionId IN (select id from @versionids)
                    versionIdIndex = reader.GetOrdinal("VersionId");
                    propertyTypeIdIndex = reader.GetOrdinal("PropertyTypeId");
                    var nodeIdIndex = reader.GetOrdinal("ReferredNodeId");

                    // Collect references to Dictionary<versionId, Dictionary<propertyTypeId, List<referredNodeId>>>
                    var referenceCollector = new Dictionary<int, Dictionary<int, List<int>>>();
                    while (reader.Read())
                    {
                        var versionId = reader.GetInt32(versionIdIndex);
                        var propertyTypeId = reader.GetInt32(propertyTypeIdIndex);
                        var referredNodeId = reader.GetInt32(nodeIdIndex);

                        if (!referenceCollector.ContainsKey(versionId))
                            referenceCollector.Add(versionId, new Dictionary<int, List<int>>());
                        var referenceCollectorPerVersion = referenceCollector[versionId];
                        if (!referenceCollectorPerVersion.ContainsKey(propertyTypeId))
                            referenceCollectorPerVersion.Add(propertyTypeId, new List<int>());
                        referenceCollectorPerVersion[propertyTypeId].Add(referredNodeId);
                    }
                    // Set references to NodeData
                    foreach (var versionId in referenceCollector.Keys)
                    {
                        var referenceCollectorPerVersion = referenceCollector[versionId];
                        foreach (var propertyTypeId in referenceCollectorPerVersion.Keys)
                            builders[versionId].AddDynamicProperty(propertyTypeId, referenceCollectorPerVersion[propertyTypeId]);
                    }

                    reader.NextResult();


                    // #4: TextPropertyInfo (NText:Lazy, NVarchar(4000):loaded)
                    // SELECT VersionId, PropertyTypeId, NULL AS Value, 0 AS Loaded
                    // FROM dbo.TextPropertiesNText
                    // WHERE VersionId IN (select id from @versionids)
                    // UNION ALL
                    // SELECT VersionId, PropertyTypeId, Value, 1 AS Loaded
                    // FROM dbo.TextPropertiesNVarchar
                    // WHERE VersionId IN (select id from @versionids)
                    versionIdIndex = reader.GetOrdinal("VersionID");
                    propertyTypeIdIndex = reader.GetOrdinal("PropertyTypeId");
                    var valueIndex = reader.GetOrdinal("Value");
                    var loadedIndex = reader.GetOrdinal("Loaded");

                    while (reader.Read())
                    {
                        int versionId = reader.GetInt32(versionIdIndex);
                        int propertyTypeId = reader.GetInt32(propertyTypeIdIndex);
                        string value = reader.GetSafeString(valueIndex); // (reader[valueIndex] == DBNull.Value) ? null : reader.GetString(valueIndex);
                        bool loaded = Convert.ToBoolean(reader.GetInt32(loadedIndex));

                        if (loaded)
                            builders[versionId].AddDynamicProperty(propertyTypeId, value);
                    }

                    reader.NextResult();


                    // #5: BaseData
                    // SELECT N.NodeId, N.NodeTypeId, N.ContentListTypeId, N.ContentListId, N.CreatingInProgress, N.IsDeleted, N.IsInherited, 
                    //    N.ParentNodeId, N.[Name], N.DisplayName, N.[Path], N.[Index], N.Locked, N.LockedById, 
                    //    N.ETag, N.LockType, N.LockTimeout, N.LockDate, N.LockToken, N.LastLockUpdate,
                    //    N.CreationDate AS NodeCreationDate, N.CreatedById AS NodeCreatedById, 
                    //    N.ModificationDate AS NodeModificationDate, N.ModifiedById AS NodeModifiedById,
                    //    N.IsSystem, OwnerId,
                    //    N.SavingState, V.ChangedData,
                    //    V.VersionId, V.MajorNumber, V.MinorNumber, V.CreationDate, V.CreatedById, 
                    //    V.ModificationDate, V.ModifiedById, V.[Status],
                    //    V.Timestamp AS VersionTimestamp
                    // FROM dbo.Nodes AS N 
                    //    INNER JOIN dbo.Versions AS V ON N.NodeId = V.NodeId
                    // WHERE V.VersionId IN (select id from @versionids)
                    nodeIdIndex = reader.GetOrdinal("NodeId");
                    var nodeTypeIdIndex = reader.GetOrdinal("NodeTypeId");
                    var contentListTypeIdIndex = reader.GetOrdinal("ContentListTypeId");
                    var contentListIdIndex = reader.GetOrdinal("ContentListId");
                    var creatingInProgressIndex = reader.GetOrdinal("CreatingInProgress");
                    var isDeletedIndex = reader.GetOrdinal("IsDeleted");
                    var parentNodeIdIndex = reader.GetOrdinal("ParentNodeId");
                    var nameIndex = reader.GetOrdinal("Name");
                    var displayNameIndex = reader.GetOrdinal("DisplayName");
                    var pathIndex = reader.GetOrdinal("Path");
                    var indexIndex = reader.GetOrdinal("Index");
                    var lockedIndex = reader.GetOrdinal("Locked");
                    var lockedByIdIndex = reader.GetOrdinal("LockedById");
                    var eTagIndex = reader.GetOrdinal("ETag");
                    var lockTypeIndex = reader.GetOrdinal("LockType");
                    var lockTimeoutIndex = reader.GetOrdinal("LockTimeout");
                    var lockDateIndex = reader.GetOrdinal("LockDate");
                    var lockTokenIndex = reader.GetOrdinal("LockToken");
                    var lastLockUpdateIndex = reader.GetOrdinal("LastLockUpdate");
                    var nodeCreationDateIndex = reader.GetOrdinal("NodeCreationDate");
                    var nodeCreatedByIdIndex = reader.GetOrdinal("NodeCreatedById");
                    var nodeModificationDateIndex = reader.GetOrdinal("NodeModificationDate");
                    var nodeModifiedByIdIndex = reader.GetOrdinal("NodeModifiedById");
                    var isSystemIndex = reader.GetOrdinal("IsSystem");
                    var ownerIdIndex = reader.GetOrdinal("OwnerId");
                    var savingStateIndex = reader.GetOrdinal("SavingState");
                    var changedDataIndex = reader.GetOrdinal("ChangedData");
                    var nodeTimestampIndex = reader.GetOrdinal("NodeTimestamp");

                    versionIdIndex = reader.GetOrdinal("VersionId");
                    var majorNumberIndex = reader.GetOrdinal("MajorNumber");
                    var minorNumberIndex = reader.GetOrdinal("MinorNumber");
                    var versionCreationDateIndex = reader.GetOrdinal("CreationDate");
                    var versionCreatedByIdIndex = reader.GetOrdinal("CreatedById");
                    var versionModificationDateIndex = reader.GetOrdinal("ModificationDate");
                    var versionModifiedByIdIndex = reader.GetOrdinal("ModifiedById");
                    var status = reader.GetOrdinal("Status");
                    var versionTimestampIndex = reader.GetOrdinal("VersionTimestamp");

                    while (reader.Read())
                    {
                        int versionId = reader.GetInt32(versionIdIndex);

                        VersionNumber versionNumber = new VersionNumber(
                            reader.GetInt16(majorNumberIndex),
                            reader.GetInt16(minorNumberIndex),
                            (VersionStatus)reader.GetInt16(status));

                        builders[versionId].SetCoreAttributes(
                            reader.GetInt32(nodeIdIndex),
                            reader.GetInt32(nodeTypeIdIndex),
                            TypeConverter.ToInt32(reader.GetValue(contentListIdIndex)),
                            TypeConverter.ToInt32(reader.GetValue(contentListTypeIdIndex)),
                            Convert.ToBoolean(reader.GetByte(creatingInProgressIndex)),
                            Convert.ToBoolean(reader.GetByte(isDeletedIndex)),
                            reader.GetSafeInt32(parentNodeIdIndex),
                            reader.GetString(nameIndex),
                            reader.GetSafeString(displayNameIndex),
                            reader.GetString(pathIndex),
                            reader.GetInt32(indexIndex),
                            Convert.ToBoolean(reader.GetByte(lockedIndex)),
                            reader.GetSafeInt32(lockedByIdIndex),
                            reader.GetString(eTagIndex),
                            reader.GetInt32(lockTypeIndex),
                            reader.GetInt32(lockTimeoutIndex),
                            reader.GetDateTimeUtc(lockDateIndex),
                            reader.GetString(lockTokenIndex),
                            reader.GetDateTimeUtc(lastLockUpdateIndex),
                            versionId,
                            versionNumber,
                            reader.GetDateTimeUtc(versionCreationDateIndex),
                            reader.GetInt32(versionCreatedByIdIndex),
                            reader.GetDateTimeUtc(versionModificationDateIndex),
                            reader.GetInt32(versionModifiedByIdIndex),
                            reader.GetSafeBooleanFromByte(isSystemIndex),
                            reader.GetSafeInt32(ownerIdIndex),
                            reader.GetSavingState(savingStateIndex),
                            reader.GetChangedData(changedDataIndex),
                            reader.GetDateTimeUtc(nodeCreationDateIndex),
                            reader.GetInt32(nodeCreatedByIdIndex),
                            reader.GetDateTimeUtc(nodeModificationDateIndex),
                            reader.GetInt32(nodeModifiedByIdIndex),
                            GetLongFromBytes((byte[])reader.GetValue(nodeTimestampIndex)),
                            GetLongFromBytes((byte[])reader.GetValue(versionTimestampIndex))
                            );
                    }
                    foreach (var builder in builders.Values)
                        builder.Finish();
                }
                finally
                {
                    if (reader != null && !reader.IsClosed)
                        reader.Close();

                    cmd.Dispose();
                }
                op.Successful = true;
            }
        }

        protected internal override bool IsCacheableText(string text)
        {
            if (text == null)
                return false;
            return text.Length < TextAlternationSizeLimit;
        }

        protected internal override string LoadTextPropertyValue(int versionId, int propertyTypeId)
        {
            SqlProcedure cmd = null;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_TextProperty_LoadValue" };
                cmd.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
                cmd.Parameters.Add("@PropertyTypeId", SqlDbType.Int).Value = propertyTypeId;
                var s = (string)cmd.ExecuteScalar();
                return s;
            }
            finally
            {
                cmd.Dispose();
            }
        }

        protected internal override Dictionary<int, string> LoadTextPropertyValues(int versionId, int[] propertyTypeIds)
        {
            var result = new Dictionary<int, string>();
            if (propertyTypeIds == null || propertyTypeIds.Length == 0)
                return result;

            var propParamPrefix = "@Prop";
            var sql = String.Format("SELECT PropertyTypeId, Value FROM TextPropertiesNText WHERE VersionId = @VersionId AND PropertyTypeId IN ({0})",
                String.Join(", ", Enumerable.Range(0, propertyTypeIds.Length).Select(i => propParamPrefix + i)));

            SqlProcedure cmd = null;
            try
            {
                cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text };

                cmd.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
                for (int i = 0; i < propertyTypeIds.Length; i++)
                    cmd.Parameters.Add(propParamPrefix + i, SqlDbType.Int).Value = propertyTypeIds[i];

                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                        result.Add(reader.GetInt32(0), reader.GetSafeString(1));
            }
            finally
            {
                cmd.Dispose();
            }
            return result;
        }

        protected internal override BinaryDataValue LoadBinaryPropertyValue(int versionId, int propertyTypeId)
        {
            BinaryDataValue result = null;
            using (var op = SnTrace.Database.StartOperation("SqlProvider.LoadBinaryPropertyValue. VId:{0}, PtId:{1}", versionId, propertyTypeId))
            {
                using (var cmd = new SqlProcedure { CommandText = "proc_BinaryProperty_LoadValue" })
                {
                    cmd.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
                    cmd.Parameters.Add("@PropertyTypeId", SqlDbType.Int).Value = propertyTypeId;
                    using (var reader = cmd.ExecuteReader())
                    {
                        // #2: BinaryProperties
                        // SELECT B.BinaryPropertyId, B.VersionId, B.PropertyTypeId, F.FileId, F.ContentType, F.FileNameWithoutExtension,
                        //     F.Extension, F.[Size], F.[Checksum], NULL AS Stream, 0 AS Loaded, F.[Timestamp]
                        // FROM dbo.BinaryProperties B
                        //     JOIN dbo.Files F ON B.FileId = F.FileId
                        // WHERE VersionId = @VersionId AND PropertyTypeId = @PropertyTypeId AND Staging IS NULL
                        var binaryPropertyIdIndex = reader.GetOrdinal("BinaryPropertyId");
                        var fileIdIndex = reader.GetOrdinal("FileId");
                        var contentTypeIndex = reader.GetOrdinal("ContentType");
                        var fileNameWithoutExtensionIndex = reader.GetOrdinal("FileNameWithoutExtension");
                        var extensionIndex = reader.GetOrdinal("Extension");
                        var sizeIndex = reader.GetOrdinal("Size");
                        var checksumPropertyIndex = reader.GetOrdinal("Checksum");
                        var timestampIndex = reader.GetOrdinal("Timestamp");

                        if (reader.Read())
                        {
                            string ext = reader.GetString(extensionIndex);
                            if (ext.Length != 0)
                                ext = ext.Remove(0, 1); // Remove dot from the start if extension is not empty
                            string fn = reader.GetSafeString(fileNameWithoutExtensionIndex);

                            result = new BinaryDataValue
                            {
                                Id = reader.GetInt32(binaryPropertyIdIndex),
                                FileId = reader.GetInt32(fileIdIndex),
                                Checksum = reader.GetSafeString(checksumPropertyIndex),
                                FileName = new BinaryFileName(fn, ext),
                                ContentType = reader.GetString(contentTypeIndex),
                                Size = reader.GetInt64(sizeIndex),
                                Timestamp = DataProvider.GetLongFromBytes((byte[])reader.GetValue(timestampIndex))
                            };
                        }
                    }
                }
                op.Successful = true;
            }
            return result;
        }

        [Obsolete("Use GetStream method on a BinaryData instance instead.", true)]
        protected internal override Stream LoadStream(int versionId, int propertyTypeId)
        {
            throw new NotSupportedException("Use GetStream method on a BinaryData instance instead.");
        }

        protected internal override IEnumerable<int> GetChildrenIdentfiers(int nodeId)
        {
            using (var cmd = new SqlProcedure { CommandText = "SELECT NodeId FROM Nodes WHERE ParentNodeId = @ParentNodeId" })
            {
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.Add("@ParentNodeId", SqlDbType.Int).Value = nodeId;
                using (var reader = cmd.ExecuteReader())
                {
                    var ids = new List<int>();
                    while (reader.Read())
                        ids.Add(reader.GetSafeInt32(0));

                    return ids;
                }
            }
        }

        //////////////////////////////////////// Operations ////////////////////////////////////////

        protected internal override IEnumerable<NodeType> LoadChildTypesToAllow(int sourceNodeId)
        {
            var result = new List<NodeType>();
            using (var cmd = new SqlProcedure { CommandText = "proc_LoadChildTypesToAllow" })
            {
                cmd.Parameters.Add("@NodeId", SqlDbType.Int).Value = sourceNodeId;
                var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var name = (string)reader[0];
                    var nt = ActiveSchema.NodeTypes[name];
                    if (nt != null)
                        result.Add(nt);
                }
            }
            return result;
        }
        protected internal override DataOperationResult MoveNodeTree(int sourceNodeId, int targetNodeId, long sourceTimestamp = 0, long targetTimestamp = 0)
        {
            SqlProcedure cmd = null;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_Node_Move" };
                cmd.Parameters.Add("@SourceNodeId", SqlDbType.Int).Value = sourceNodeId;
                cmd.Parameters.Add("@TargetNodeId", SqlDbType.Int).Value = targetNodeId;
                cmd.Parameters.Add("@SourceTimestamp", SqlDbType.Timestamp).Value = sourceTimestamp == 0 ? DBNull.Value : (object)GetBytesFromLong(sourceTimestamp);
                cmd.Parameters.Add("@TargetTimestamp", SqlDbType.Timestamp).Value = targetTimestamp == 0 ? DBNull.Value : (object)GetBytesFromLong(targetTimestamp);
                cmd.ExecuteNonQuery();
            }
            catch (SqlException e) // logged // rethrow
            {
                if (e.Message.StartsWith("Source node is out of date"))
                {
                    StorageContext.L2Cache.Clear();
                    throw new NodeIsOutOfDateException(e.Message, e);
                }

                if (e.Message.StartsWith("String or binary data would be truncated"))
                    return DataOperationResult.DataTooLong;

                switch (e.State)
                {
                    case 1: // 'Invalid operation: moving a contentList / a subtree that contains a contentList under an another contentList.'
                        SnLog.WriteException(e);
                        return DataOperationResult.Move_TargetContainsSameName;
                    case 2:
                        return DataOperationResult.Move_NodeWithContentListContentUnderContentList;
                    default:
                        throw;
                }
            }
            finally
            {
                cmd.Dispose();
            }
            return 0;
        }

        protected internal override DataOperationResult DeleteNodeTree(int nodeId)
        {
            SqlProcedure cmd = null;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_Node_Delete" };
                cmd.Parameters.Add("@NodeId", SqlDbType.Int).Value = nodeId;
                cmd.ExecuteNonQuery();
            }
            finally
            {
                cmd.Dispose();
            }
            return DataOperationResult.Successful;
        }

        protected internal override DataOperationResult DeleteNodeTreePsychical(int nodeId, long timestamp = 0)
        {
            SqlProcedure cmd = null;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_Node_DeletePhysical" };
                cmd.Parameters.Add("@NodeId", SqlDbType.Int).Value = nodeId;
                cmd.Parameters.Add("@Timestamp", SqlDbType.Timestamp).Value = timestamp == 0 ? DBNull.Value : (object)GetBytesFromLong(timestamp);
                cmd.ExecuteNonQuery();
            }
            catch (SqlException e) // rethrow
            {
                if (e.Message.StartsWith("Node is out of date"))
                {
                    StorageContext.L2Cache.Clear();
                    throw new NodeIsOutOfDateException(e.Message, e);
                }
                throw;
            }
            finally
            {
                cmd.Dispose();
            }
            return DataOperationResult.Successful;
        }

        protected internal override void DeleteVersion(int versionId, NodeData nodeData, out int lastMajorVersionId, out int lastMinorVersionId)
        {
            SqlProcedure cmd = null;
            SqlDataReader reader = null;
            lastMajorVersionId = 0;
            lastMinorVersionId = 0;

            try
            {
                cmd = new SqlProcedure { CommandText = "proc_Node_DeleteVersion" };
                cmd.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;

                reader = cmd.ExecuteReader();

                // refresh timestamp value from the db
                while (reader.Read())
                {
                    nodeData.NodeTimestamp = DataProvider.GetLongFromBytes((byte[])reader[0]);
                    lastMajorVersionId = reader.GetSafeInt32(1);
                    lastMinorVersionId = reader.GetSafeInt32(2);
                }
            }
            finally
            {
                if (reader != null && !reader.IsClosed)
                    reader.Close();
                cmd.Dispose();
            }
        }

        protected internal override bool HasChild(int nodeId)
        {
            SqlProcedure cmd = null;
            int result;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_Node_HasChild" };
                cmd.Parameters.Add("@NodeId", SqlDbType.Int).Value = nodeId;
                result = (int)cmd.ExecuteScalar();
            }
            finally
            {
                cmd.Dispose();
            }

            if (result == -1)
                throw new ApplicationException();

            return result > 0;
        }

        private const string SELECTCONTENTLISTTYPESSCRIPT = @"SELECT ContentListTypeId FROM Nodes WHERE ContentListId IS NULL AND ContentListTypeId IS NOT NULL AND Path LIKE REPLACE(@Path, '_', '[_]') + '/%' COLLATE Latin1_General_CI_AS";

        protected internal override List<ContentListType> GetContentListTypesInTree(string path)
        {
            SqlProcedure cmd = null;
            SqlDataReader reader = null;
            var result = new List<ContentListType>();

            cmd = new SqlProcedure
            {
                CommandText = SELECTCONTENTLISTTYPESSCRIPT,
                CommandType = CommandType.Text
            };
            cmd.Parameters.Add("@Path", SqlDbType.NVarChar, 450).Value = path;
            try
            {
                reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt32(0);
                    var t = NodeTypeManager.Current.ContentListTypes.GetItemById(id);
                    result.Add(t);
                }
            }
            finally
            {
                if (reader != null)
                    reader.Dispose();
                if (cmd != null)
                    cmd.Dispose();
            }
            return result;
        }

        protected internal override bool IsFilestreamEnabled()
        {
            bool fsEnabled;

            using (var pro = CreateDataProcedure("SELECT COUNT(name) FROM sys.columns WHERE Name = N'FileStream' and Object_ID = Object_ID(N'Files')"))
            {
                pro.CommandType = CommandType.Text;

                try
                {
                    fsEnabled = Convert.ToInt32(pro.ExecuteScalar()) > 0;
                }
                catch (Exception ex)
                {
                    SnLog.WriteException(ex);

                    fsEnabled = false;
                }
            }

            return fsEnabled;
        }

        //////////////////////////////////////// Chunk upload ////////////////////////////////////////

        protected internal override void CopyFromStream(int versionId, string token, Stream input)
        {
            BlobStorage.CopyFromStream(versionId, token, input);
        }

        protected internal override string StartChunk(int versionId, int propertyTypeId, long fullSize)
        {
            return BlobStorage.StartChunk(versionId, propertyTypeId, fullSize);
        }

        protected internal override void WriteChunk(int versionId, string token, byte[] buffer, long offset, long fullSize)
        {
            BlobStorage.WriteChunk(versionId, token, buffer, offset, fullSize);
        }

        protected internal override void CommitChunk(int versionId, int propertyTypeId, string token, long fullSize, BinaryDataValue source = null)
        {
            BlobStorage.CommitChunk(versionId, propertyTypeId, token, fullSize, source);
        }



        internal static string CreateIdXmlForReferencePropertyUpdate(IEnumerable<int> values)
        {
            StringBuilder xmlBuilder = new StringBuilder(values == null ? 50 : 50 + values.Count() * 10);
            xmlBuilder.AppendLine("<Identifiers>");
            xmlBuilder.AppendLine("<ReferredNodeIds>");
            if (values != null)
                foreach (var value in values)
                    if (value > 0)
                        xmlBuilder.Append("<Id>").Append(value).AppendLine("</Id>");
            xmlBuilder.AppendLine("</ReferredNodeIds>");
            xmlBuilder.AppendLine("</Identifiers>");
            return xmlBuilder.ToString();
        }

        private static string CreateIdXmlForNodeInfoBatchLoad(Dictionary<int, NodeBuilder> builders)
        {
            StringBuilder xmlBuilder = new StringBuilder(500 + builders.Count * 20);

            xmlBuilder.AppendLine("<Identifiers>");
            xmlBuilder.AppendLine("  <VersionIds>");
            foreach (int versionId in builders.Keys)
                xmlBuilder.Append("    <Id>").Append(versionId).AppendLine("</Id>");
            xmlBuilder.AppendLine("  </VersionIds>");
            xmlBuilder.AppendLine("</Identifiers>");

            return xmlBuilder.ToString();
        }

        protected internal override long GetTreeSize(string path, bool includeChildren)
        {
            SqlProcedure cmd = null;
            long result;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_Node_GetTreeSize" };
                cmd.Parameters.Add("@NodePath", SqlDbType.NVarChar, 450).Value = path;
                cmd.Parameters.Add("@IncludeChildren", SqlDbType.TinyInt).Value = includeChildren ? 1 : 0;

                var obj = cmd.ExecuteScalar();

                result = (obj == DBNull.Value) ? 0 : (long)obj;
            }
            catch (Exception ex)
            {
                throw ex;
            }
            finally
            {
                if (cmd != null)
                    cmd.Dispose();
            }

            if (result == -1)
                throw new ApplicationException();

            return result;
        }

        protected override int NodeCount(string path)
        {
            var proc = new SqlProcedure();
            proc.CommandType = CommandType.Text;
            if (String.IsNullOrEmpty(path) || path == RepositoryPath.PathSeparator)
            {
                proc.CommandText = "SELECT COUNT(*) FROM Nodes";
            }
            else
            {
                proc.CommandText = "SELECT COUNT(*) FROM Nodes WHERE Path LIKE REPLACE(@Path, '_', '[_]') + '/%' COLLATE Latin1_General_CI_AS";
                proc.Parameters.Add("@Path", SqlDbType.NVarChar, 450).Value = path;
            }
            return (int)proc.ExecuteScalar();
        }
        protected override int VersionCount(string path)
        {
            var proc = new SqlProcedure();
            proc.CommandType = CommandType.Text;
            if (String.IsNullOrEmpty(path) || path == RepositoryPath.PathSeparator)
            {
                proc.CommandText = "SELECT COUNT(*) FROM Versions V JOIN Nodes N ON N.NodeId = V.NodeId";
            }
            else
            {
                proc.CommandText = "SELECT COUNT(*) FROM Versions V JOIN Nodes N ON N.NodeId = V.NodeId WHERE N.Path LIKE REPLACE(@Path, '_', '[_]') + '/%' COLLATE Latin1_General_CI_AS";
                proc.Parameters.Add("@Path", SqlDbType.NVarChar, 450).Value = path;
            }
            return (int)proc.ExecuteScalar();
        }

        //////////////////////////////////////// Security Methods ////////////////////////////////////////

        protected internal override void InstallDefaultSecurityStructure()
        {
            var securityContext = SecurityHandler.SecurityContext;
            SecurityHandler.DeleteEverythingAndRestart();

            using (var proc = CreateDataProcedure("SELECT NodeId, ParentNodeId, [OwnerId] FROM Nodes ORDER BY [Path]"))
            {
                proc.CommandType = CommandType.Text;
                using (var reader = proc.ExecuteReader())
                {
                    var idSet = new List<int>();
                    while (reader.Read())
                    {
                        var id = reader.GetSafeInt32(0);
                        var parentId = reader.GetSafeInt32(1);
                        var ownerId = reader.GetSafeInt32(2);
                        securityContext.CreateSecurityEntity(id, parentId, ownerId);
                    }
                }
            }
        }

        // ======================================================

        protected internal override NodeHead LoadNodeHead(string path)
        {
            return LoadNodeHead(0, path, 0);
        }
        protected internal override NodeHead LoadNodeHead(int nodeId)
        {
            return LoadNodeHead(nodeId, null, 0);
        }
        protected internal override NodeHead LoadNodeHeadByVersionId(int versionId)
        {
            return LoadNodeHead(0, null, versionId);
        }
        private NodeHead LoadNodeHead(int nodeId, string path, int versionId)
        {
            SqlProcedure cmd = null;
            SqlDataReader reader = null;

            // command string sceleton. When using this, WHERE clause needs to be completed!
            string commandString = @"
                    SELECT
                        Node.NodeId,             -- 0
	                    Node.Name,               -- 1
	                    Node.DisplayName,        -- 2
                        Node.Path,               -- 3
                        Node.ParentNodeId,       -- 4
                        Node.NodeTypeId,         -- 5
	                    Node.ContentListTypeId,  -- 6
	                    Node.ContentListId,      -- 7
                        Node.CreationDate,       -- 8
                        Node.ModificationDate,   -- 9
                        Node.LastMinorVersionId, -- 10
                        Node.LastMajorVersionId, -- 11
                        Node.OwnerId,            -- 12
                        Node.CreatedById,        -- 13
                        Node.ModifiedById,       -- 14
  		                Node.[Index],            -- 15
		                Node.LockedById,         -- 16
                        Node.Timestamp           -- 17
                    FROM
	                    Nodes Node  
                    WHERE ";
            if (path != null)
            {
                commandString = string.Concat(commandString, "Node.Path = @Path COLLATE Latin1_General_CI_AS");
            }
            else if (versionId > 0)
            {
                commandString = string.Concat(@"DECLARE @NodeId int
                    SELECT @NodeId = NodeId FROM Versions WHERE VersionId = @VersionId
                ",
                 commandString,
                 "Node.NodeId = @NodeId");
            }
            else
            {
                commandString = string.Concat(commandString, "Node.NodeId = @NodeId");
            }

            cmd = new SqlProcedure { CommandText = commandString };
            cmd.CommandType = CommandType.Text;
            if (path != null)
                cmd.Parameters.Add("@Path", SqlDbType.NVarChar, 450).Value = path;
            else if (versionId > 0)
                cmd.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
            else
                cmd.Parameters.Add("@NodeId", SqlDbType.Int).Value = nodeId;

            try
            {
                reader = cmd.ExecuteReader();
                if (!reader.Read())
                    return null;

                return new NodeHead(
                    reader.GetInt32(0),         // nodeId,
                    reader.GetString(1),        // name,
                    reader.GetSafeString(2),    // displayName,
                    reader.GetString(3),        // pathInDb,
                    reader.GetSafeInt32(4),     // parentNodeId,
                    reader.GetInt32(5),         // nodeTypeId,
                    reader.GetSafeInt32(6),     // contentListTypeId,
                    reader.GetSafeInt32(7),     // contentListId,
                    reader.GetDateTimeUtc(8),   // creationDate,
                    reader.GetDateTimeUtc(9),   // modificationDate,
                    reader.GetSafeInt32(10),    // lastMinorVersionId,
                    reader.GetSafeInt32(11),    // lastMajorVersionId,
                    reader.GetSafeInt32(12),    // ownerId,
                    reader.GetSafeInt32(13),    // creatorId,
                    reader.GetSafeInt32(14),    // modifierId,
                    reader.GetSafeInt32(15),    // index,
                    reader.GetSafeInt32(16),    // lockerId
                    GetLongFromBytes((byte[])reader.GetValue(17))     // timestamp
                );

            }
            finally
            {
                if (reader != null)
                    reader.Dispose();
                if (cmd != null)
                    cmd.Dispose();
            }
        }
        protected internal override IEnumerable<NodeHead> LoadNodeHeads(IEnumerable<int> heads)
        {
            var nodeHeads = new List<NodeHead>();


            var cn = new SqlConnection(ConnectionStrings.ConnectionString);
            var cmd = new SqlCommand
            {
                Connection = cn,
                CommandTimeout = Configuration.Data.SqlCommandTimeout,
                CommandType = CommandType.StoredProcedure,
                CommandText = "proc_NodeHead_Load_Batch"
            };

            var sb = new StringBuilder();
            sb.Append("<NodeHeads>");
            foreach (var id in heads)
                sb.Append("<id>").Append(id).Append("</id>");
            sb.Append("</NodeHeads>");

            cmd.Parameters.Add("@IdsInXml", SqlDbType.Xml).Value = sb.ToString();
            var adapter = new SqlDataAdapter(cmd);
            var dataSet = new DataSet();

            try
            {
                cn.Open();
                adapter.Fill(dataSet);
            }
            finally
            {
                cn.Close();
            }

            if (dataSet.Tables[0].Rows.Count > 0)
                foreach (DataRow currentRow in dataSet.Tables[0].Rows)
                {
                    if (currentRow["NodeID"] == DBNull.Value)
                        nodeHeads.Add(null);
                    else
                        nodeHeads.Add(new NodeHead(
                            TypeConverter.ToInt32(currentRow["NodeID"]),  //  0 - NodeId
                            TypeConverter.ToString(currentRow[1]),        //  1 - Name
                            TypeConverter.ToString(currentRow[2]),        //  2 - DisplayName
                            TypeConverter.ToString(currentRow[3]),        //  3 - Path
                            TypeConverter.ToInt32(currentRow[4]),         //  4 - ParentNodeId
                            TypeConverter.ToInt32(currentRow[5]),         //  5 - NodeTypeId
                            TypeConverter.ToInt32(currentRow[6]),         //  6 - ContentListTypeId 
                            TypeConverter.ToInt32(currentRow[7]),         //  7 - ContentListId
                            TypeConverter.ToDateTime(currentRow[8]),      //  8 - CreationDate
                            TypeConverter.ToDateTime(currentRow[9]),      //  9 - ModificationDate
                            TypeConverter.ToInt32(currentRow[10]),        // 10 - LastMinorVersionId
                            TypeConverter.ToInt32(currentRow[11]),        // 11 - LastMajorVersionId
                            TypeConverter.ToInt32(currentRow[12]),        // 12 - OwnerId
                            TypeConverter.ToInt32(currentRow[13]),        // 12 - CreatedById
                            TypeConverter.ToInt32(currentRow[14]),        // 13 - ModifiedById
                            TypeConverter.ToInt32(currentRow[15]),        // 14 - Index
                            TypeConverter.ToInt32(currentRow[16]),        // 15 - LockedById
                            GetLongFromBytes((byte[])currentRow[17])
                            ));

                }
            return nodeHeads;
        }

        protected internal override NodeHead.NodeVersion[] GetNodeVersions(int nodeId)
        {
            SqlProcedure cmd = null;
            SqlDataReader reader = null;
            try
            {
                string commandString = @"
                    SELECT VersionId, MajorNumber, MinorNumber, Status
                    FROM Versions
                    WHERE NodeId = @NodeId
                    ORDER BY MajorNumber, MinorNumber
                ";
                cmd = new SqlProcedure { CommandText = commandString };
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.Add("@NodeId", SqlDbType.NVarChar, 450).Value = nodeId;
                reader = cmd.ExecuteReader();

                List<NodeHead.NodeVersion> versionList = new List<NodeHead.NodeVersion>();

                while (reader.Read())
                {
                    var versionId = reader.GetInt32(0);
                    var major = reader.GetInt16(1);
                    var minor = reader.GetInt16(2);
                    var statusCode = reader.GetInt16(3);

                    var versionNumber = new VersionNumber(major, minor, (VersionStatus)statusCode);

                    versionList.Add(new NodeHead.NodeVersion(versionNumber, versionId));
                }

                return versionList.ToArray();

            }
            finally
            {
                if (reader != null)
                    reader.Dispose();
                if (cmd != null)
                    cmd.Dispose();
            }


        }

        protected internal override BinaryCacheEntity LoadBinaryCacheEntity(int nodeVersionId, int propertyTypeId)
        {
            return BlobStorage.LoadBinaryCacheEntity(nodeVersionId, propertyTypeId);
        }
        protected internal override byte[] LoadBinaryFragment(int fileId, long position, int count)
        {
            return BlobStorage.LoadBinaryFragment(fileId, position, count);
        }

        protected internal override BlobStorageContext GetBlobStorageContext(int fileId, bool clearStream = false, int versionId = 0, int propertyTypeId = 0)
        {
            return BlobStorage.GetBlobStorageContext(fileId, clearStream, versionId, propertyTypeId);
        }

        protected override bool NodeExistsInDatabase(string path)
        {
            var cmd = new SqlProcedure { CommandText = "SELECT COUNT(*) FROM Nodes WHERE Path = @Path COLLATE Latin1_General_CI_AS", CommandType = CommandType.Text };
            cmd.Parameters.Add("@Path", SqlDbType.NVarChar, PathMaxLength).Value = path;
            try
            {
                var count = (int)cmd.ExecuteScalar();
                return count > 0;
            }
            finally
            {
                if (cmd != null)
                    cmd.Dispose();
            }
        }
        public override string GetNameOfLastNodeWithNameBase(int parentId, string namebase, string extension)
        {
            var cmd = new SqlProcedure
            {
                CommandText = @"
DECLARE @NameEscaped nvarchar(450)
SET @NameEscaped = REPLACE(@Name, '_', '[_]')
SELECT TOP 1 Name FROM Nodes WHERE ParentNodeId=@ParentId AND (
	Name LIKE @NameEscaped + '([0-9])' + @Extension OR
	Name LIKE @NameEscaped + '([0-9][0-9])' + @Extension OR
	Name LIKE @NameEscaped + '([0-9][0-9][0-9])' + @Extension OR
	Name LIKE @NameEscaped + '([0-9][0-9][0-9][0-9])' + @Extension
)
ORDER BY LEN(Name) DESC, Name DESC",
                CommandType = CommandType.Text
            };
            cmd.Parameters.Add("@ParentId", SqlDbType.Int).Value = parentId;
            cmd.Parameters.Add("@Name", SqlDbType.NVarChar).Value = namebase;
            cmd.Parameters.Add("@Extension", SqlDbType.NVarChar).Value = extension;
            try
            {
                var lastName = (string)cmd.ExecuteScalar();
                return lastName;
            }
            finally
            {
                if (cmd != null)
                    cmd.Dispose();
            }
        }

        public override DateTime RoundDateTime(DateTime d)
        {
            return new DateTime(d.Ticks / 100000 * 100000);
        }

        // ====================================================== AppModel script generator

        #region AppModel script generator constants
        private const string AppModelQ0 = "DECLARE @availablePaths AS TABLE([Id] INT IDENTITY (1, 1), [Path] NVARCHAR(900))";
        private const string AppModelQ1 = "INSERT @availablePaths ([Path]) VALUES ('{0}')";

        private const string AppModelQ2 = @"SELECT TOP 1 N.NodeId FROM @availablePaths C
LEFT OUTER JOIN Nodes N ON C.[Path] = N.[Path]
WHERE N.[Path] IS NOT NULL
ORDER BY C.Id";

        private const string AppModelQ3 = @"SELECT N.NodeId FROM @availablePaths C
LEFT OUTER JOIN Nodes N ON C.[Path] = N.[Path]
WHERE N.[Path] IS NOT NULL
ORDER BY C.Id";

        private const string AppModelQ4 = @"SELECT N.NodeId, N.[Path] FROM Nodes N
WHERE N.ParentNodeId IN
(
    SELECT N.NodeId FROM @availablePaths C
    LEFT OUTER JOIN Nodes N ON C.[Path] = N.[Path]
    WHERE N.[Path] IS NOT NULL
)";
        #endregion

        protected override string GetAppModelScriptPrivate(IEnumerable<string> paths, bool resolveAll, bool resolveChildren)
        {
            var script = new StringBuilder();
            script.AppendLine(AppModelQ0);
            foreach (var path in paths)
            {
                script.AppendFormat(AppModelQ1, SecureSqlStringValue(path));
                script.AppendLine();
            }

            if (resolveAll)
            {
                if (resolveChildren)
                    script.AppendLine(AppModelQ4);
                else
                    script.AppendLine(AppModelQ3);
            }
            else
            {
                script.Append(AppModelQ2);
            }
            return script.ToString();
        }

        /// <summary>
        /// SQL injection prevention.
        /// </summary>
        /// <param name="value">String value that will changed to.</param>
        /// <returns>Safe string value.</returns>
        public static string SecureSqlStringValue(string value)
        {
            return value.Replace(@"'", @"''").Replace("/*", "**").Replace("--", "**");
        }

        // ====================================================== Custom database script support

        protected internal override IDataProcedure CreateDataProcedureInternal(string commandText, string connectionName = null, InitialCatalog initialCatalog = InitialCatalog.Initial)
        {
            var proc = new SqlProcedure(connectionName, initialCatalog)
            {
                CommandText = commandText
            };
            return proc;
        }
        protected internal override IDataProcedure CreateDataProcedureInternal(string commandText, ConnectionInfo connectionInfo)
        {
            var proc = new SqlProcedure(connectionInfo)
            {
                CommandText = commandText
            };
            return proc;
        }
        protected override IDbDataParameter CreateParameterInternal()
        {
            return new SqlParameter();
        }

        protected internal override void CheckScriptInternal(string commandText)
        {
            // c:\Program Files\Microsoft SQL Server\90\SDK\Assemblies\Microsoft.SqlServer.Smo.dll
            // c:\Program Files\Microsoft SQL Server\90\SDK\Assemblies\Microsoft.SqlServer.ConnectionInfo.dll

            // The code maybe equivalent to this script:
            // SET NOEXEC ON
            // GO
            // SELECT * FROM Nodes
            // GO
            // SET NOEXEC OFF
            // GO
        }

        // ====================================================== Index document save / load operations

        private const string LOADINDEXDOCUMENTSCRIPT = @"
            SELECT N.NodeTypeId, V.VersionId, V.NodeId, N.ParentNodeId, N.Path, N.IsSystem, N.LastMinorVersionId, N.LastMajorVersionId, V.Status, 
                V.IndexDocument, N.Timestamp, V.Timestamp
            FROM Nodes N INNER JOIN Versions V ON N.NodeId = V.NodeId
            ";

        private const string LOADIDSWITHEMPTYINDEXDOCUMENTSCRIPT = @"DECLARE @temp table (NodeId int, IndexDocument nvarchar(MAX))
INSERT @temp (NodeId, IndexDocument) 
SELECT NodeId, IndexDocument
FROM Versions (NOLOCK) WHERE NodeId >= @FromId AND NodeId <= @ToId

SELECT NodeId FROM @temp
WHERE IndexDocument IS NULL";

        private const int DOCSFRAGMENTSIZE = 100;

        protected internal override void UpdateIndexDocument(NodeData nodeData, byte[] indexDocumentBytes)
        {
            using (var cmd = (SqlProcedure)CreateDataProcedure("UPDATE Versions SET [IndexDocument] = @IndexDocument WHERE VersionId = @VersionId\nSELECT Timestamp FROM Versions WHERE VersionId = @VersionId"))
            {
                cmd.CommandType = CommandType.Text;

                cmd.Parameters.Add("@VersionId", SqlDbType.Int).Value = nodeData.VersionId;
                cmd.Parameters.Add("@IndexDocument", SqlDbType.VarBinary).Value = indexDocumentBytes;

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        // SELECT Timestamp FROM Versions WHERE VersionId = @VersionId
                        nodeData.VersionTimestamp = DataProvider.GetLongFromBytes((byte[])reader[0]);
                    }
                }
            }
        }
        protected internal override void UpdateIndexDocument(int versionId, byte[] indexDocumentBytes)
        {
            using (var cmd = (SqlProcedure)CreateDataProcedure("UPDATE Versions SET [IndexDocument] = @IndexDocument WHERE VersionId = @VersionId"))
            {
                cmd.CommandType = CommandType.Text;

                cmd.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
                cmd.Parameters.Add("@IndexDocument", SqlDbType.VarBinary).Value = indexDocumentBytes;
                cmd.ExecuteNonQuery();
            }
        }

        protected internal override IndexDocumentData LoadIndexDocumentByVersionId(int versionId)
        {
            using (var cmd = new SqlProcedure { CommandText = LOADINDEXDOCUMENTSCRIPT + "WHERE V.VersionId = @VersionId" })
            {
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return GetIndexDocumentDataFromReader(reader);
                    return null;
                }
            }
        }
        protected internal override IEnumerable<IndexDocumentData> LoadIndexDocumentByVersionId(IEnumerable<int> versionId)
        {
            var fi = 0;
            var listCount = versionId.Count();
            var result = new List<IndexDocumentData>();

            while (fi * DOCSFRAGMENTSIZE < listCount)
            {
                var docsSegment = versionId.Skip(fi * DOCSFRAGMENTSIZE).Take(DOCSFRAGMENTSIZE).ToArray();
                var paramNames = docsSegment.Select((s, i) => "@vi" + i.ToString()).ToArray();
                var where = String.Concat("WHERE V.VersionId IN (", string.Join(", ", paramNames), ")");

                SqlProcedure cmd = null;
                var retry = 0;
                while (retry < 15)
                {
                    try
                    {
                        cmd = new SqlProcedure { CommandText = LOADINDEXDOCUMENTSCRIPT + where };
                        cmd.CommandType = CommandType.Text;
                        for (var i = 0; i < paramNames.Length; i++)
                        {
                            cmd.Parameters.AddWithValue(paramNames[i], docsSegment[i]);
                        }

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                                result.Add(GetIndexDocumentDataFromReader(reader));
                        }
                        break;
                    }
                    catch (Exception ex)
                    {
                        SnLog.WriteException(ex);
                        retry++;
                        System.Threading.Thread.Sleep(1000);
                    }
                    finally
                    {
                        if (cmd != null)
                            cmd.Dispose();
                    }
                }

                fi++;
            }

            return result;
        }

        private readonly string LoadIndexDocumentCollectionBlockByPath_Script = @";WITH IndexDocumentsRanked AS (
    SELECT N.NodeTypeId, V.VersionId, V.NodeId, N.ParentNodeId, N.Path, N.IsSystem, N.LastMinorVersionId, N.LastMajorVersionId,
        V.STATUS,V.IndexDocument,N.TIMESTAMP AS NodeTimeStamp, V.TIMESTAMP AS VersionTimeStamp,
        ROW_NUMBER() OVER ( ORDER BY Path ) AS RowNum
    FROM Nodes N INNER JOIN Versions V ON N.NodeId = V.NodeId
    WHERE N.NodeTypeId NOT IN ({0})
        AND (Path = @Path COLLATE Latin1_General_CI_AS OR Path LIKE REPLACE(@Path, '_', '[_]') + '/%' COLLATE Latin1_General_CI_AS)
)
SELECT * FROM IndexDocumentsRanked WHERE RowNum BETWEEN @Offset + 1 AND @Offset + @Count
";
        private DbCommand CreateLoadIndexDocumentCollectionBlockByPathProcedure(int offset, int count, string path, int[] excludedNodeTypes)
        {
            var cn = new SqlConnection(ConnectionStrings.ConnectionString);
            var cm = new SqlCommand
            {
                Connection = cn,
                CommandType = CommandType.Text,
                CommandTimeout = Configuration.Data.SqlCommandTimeout,
                CommandText = string.Format(LoadIndexDocumentCollectionBlockByPath_Script, string.Join(", ", excludedNodeTypes))
            };
            cm.Parameters.Add(new SqlParameter { ParameterName = "@Path", SqlDbType = SqlDbType.NVarChar, Size = PathMaxLength, Value = path });
            cm.Parameters.Add(new SqlParameter { ParameterName = "@Offset", SqlDbType = SqlDbType.Int, Value = offset });
            cm.Parameters.Add(new SqlParameter { ParameterName = "@Count", SqlDbType = SqlDbType.Int, Value = count });
            return cm;
        }
        protected internal override IndexDocumentData GetIndexDocumentDataFromReader(System.Data.Common.DbDataReader reader)
        {
            // 0           1          2       3             4     5         6                   7                   8       9              10         11
            // NodeTypeId, VersionId, NodeId, ParentNodeId, Path, IsSystem, LastMinorVersionId, LastMajorVersionId, Status, IndexDocument, Timestamp, Timestamp

            var versionId = reader.GetSafeInt32(1);
            var approved = Convert.ToInt32(reader.GetInt16(8)) == (int)VersionStatus.Approved;
            var isLastMajor = reader.GetSafeInt32(7) == versionId;

            var bytesData = reader.GetValue(9);
            var bytes = (bytesData == DBNull.Value) ? new byte[0] : (byte[])bytesData;

            return new IndexDocumentData(null, bytes)
            {
                NodeTypeId = reader.GetSafeInt32(0),
                VersionId = versionId,
                NodeId = reader.GetSafeInt32(2),
                ParentId = reader.GetSafeInt32(3),
                Path = reader.GetSafeString(4),
                IsSystem = reader.GetSafeBooleanFromByte(5),
                IsLastDraft = reader.GetSafeInt32(6) == versionId,
                IsLastPublic = approved && isLastMajor,
                NodeTimestamp = GetLongFromBytes((byte[])reader[10]),
                VersionTimestamp = GetLongFromBytes((byte[])reader[11]),
            };
        }
        protected internal override IEnumerable<int> GetIdsOfNodesThatDoNotHaveIndexDocument(int fromId, int toId)
        {
            using (var proc = CreateDataProcedure(LOADIDSWITHEMPTYINDEXDOCUMENTSCRIPT))
            {
                var param1 = CreateParameter();
                var param2 = CreateParameter();
                param1.ParameterName = "@FromId";
                param2.ParameterName = "@ToId";
                param1.Value = fromId;
                param2.Value = toId;
                proc.Parameters.Add(param1);
                proc.Parameters.Add(param2);

                proc.CommandType = CommandType.Text;

                using (var reader = proc.ExecuteReader())
                {
                    var idSet = new List<int>();
                    while (reader.Read())
                        idSet.Add(reader.GetSafeInt32(0));

                    return idSet;
                }
            }
        }

        protected internal override IEnumerable<IndexDocumentData> LoadIndexDocumentsByPath(string path, int[] excludedNodeTypes)
        {
            var offset = 0;
            var blockSize = IndexBlockSize;
            List<IndexDocumentData> buffer;

            while (LoadNextIndexDocumentBlock(offset, blockSize, path, excludedNodeTypes, out buffer))
            {
                foreach (var indexDocData in buffer)
                {
                    yield return indexDocData;
                }
                offset += blockSize;
            }
        }

        private bool LoadNextIndexDocumentBlock(int offset, int blockSize, string path, int[] excludedNodeTypes, out List<IndexDocumentData> buffer)
        {
            buffer = new List<IndexDocumentData>(blockSize);

            using (var proc = this.CreateLoadIndexDocumentCollectionBlockByPathProcedure(offset, blockSize, path, excludedNodeTypes))
            {
                try
                {
                    proc.Connection.Open();
                    using (var reader = proc.ExecuteReader())
                    {
                        if (!reader.HasRows)
                            return false;

                        while (reader.Read())
                        {
                            buffer.Add(GetIndexDocumentDataFromReader(reader));
                        }
                    }
                }
                catch (Exception ex) // logged, rethrown
                {
                    SnLog.WriteException(ex, string.Format("Loading index document block failed. Offset: {0}, Path: {1}", offset, path));
                    throw;
                }
                finally
                {
                    proc.Connection.Close();
                }
                return true;
            }
        }

        // ====================================================== Index backup / restore operations

        private const int BUFFERSIZE = 1024 * 128; // * 512; // * 64; // * 8;

        protected internal override IndexBackup LoadLastBackup()
        {
            var sql = @"
SELECT [IndexBackupId], [BackupNumber], [BackupDate], [ComputerName], [AppDomain],
        DATALENGTH([BackupFile]) AS [BackupFileLength], [RowGuid], [Timestamp]
    FROM [IndexBackup] WHERE IsActive != 0
SELECT [IndexBackupId], [BackupNumber], [BackupDate], [ComputerName], [AppDomain],
        DATALENGTH([BackupFile]) AS [BackupFileLength], [RowGuid], [Timestamp]
    FROM [IndexBackup2] WHERE IsActive != 0
";
            IndexBackup result = null;
            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                using (var reader = cmd.ExecuteReader())
                {
                    do
                    {
                        while (reader.Read())
                            result = GetBackupFromReader(reader);
                    } while (reader.NextResult());
                }
            }
            return result;
        }
        protected internal override IndexBackup CreateBackup(int backupNumber)
        {
            var backup = new IndexBackup
            {
                BackupNumber = backupNumber,
                AppDomainName = AppDomain.CurrentDomain.FriendlyName,
                BackupDate = DateTime.UtcNow,
                ComputerName = Environment.MachineName,
            };

            var sql = String.Format(@"INSERT INTO {0} (BackupNumber, IsActive, BackupDate, ComputerName, [AppDomain]) VALUES
                (@BackupNumber, 0, @BackupDate, @ComputerName, @AppDomain)", backup.TableName);

            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                cmd.Parameters.Add("@BackupNumber", SqlDbType.Int).Value = backup.BackupNumber;
                cmd.Parameters.Add("@BackupDate", SqlDbType.DateTime).Value = backup.BackupDate;
                cmd.Parameters.Add("@ComputerName", SqlDbType.NVarChar, 100).Value = backup.ComputerName;
                cmd.Parameters.Add("@AppDomain", SqlDbType.NVarChar, 500).Value = backup.AppDomainName;

                cmd.ExecuteNonQuery();
            }
            return backup;
        }
        private IndexBackup GetBackupFromReader(SqlDataReader reader)
        {
            var result = new IndexBackup();
            result.IndexBackupId = reader.GetInt32(0);              // IndexBackupId
            result.BackupNumber = reader.GetInt32(1);               // BackupNumber
            result.BackupDate = reader.GetDateTimeUtc(2);              // BackupDate
            result.ComputerName = reader.GetSafeString(3);          // ComputerName
            result.AppDomainName = reader.GetSafeString(4);         // AppDomain
            result.BackupFileLength = reader.GetInt64(5);           // BackupFileLength
            result.RowGuid = reader.GetGuid(6);                     // RowGuid
            result.Timestamp = GetLongFromBytes((byte[])reader[7]); // Timestamp
            return result;
        }
        protected internal override void StoreBackupStream(string backupFilePath, IndexBackup backup, IndexBackupProgress progress)
        {
            var fileLength = new FileInfo(backupFilePath).Length;

            using (var writeCommand = CreateWriteCommand(backup))
            {
                using (var stream = new FileStream(backupFilePath, FileMode.Open))
                {
                    using (var reader = new BinaryReader(stream))
                    {
                        InitializeNewStream(backup);

                        progress.Type = IndexBackupProgressType.Storing;
                        progress.Message = "Storing backup";
                        progress.MaxValue = fileLength;

                        var timer = Stopwatch.StartNew();

                        var offset = 0L;
                        while (offset < fileLength)
                        {
                            progress.Value = offset;
                            progress.NotifyChanged();

                            var remnant = fileLength - offset;
                            var length = remnant < BUFFERSIZE ? Convert.ToInt32(remnant) : BUFFERSIZE;
                            var buffer = reader.ReadBytes(length);
                            writeCommand.Parameters["@Buffer"].Value = buffer;
                            writeCommand.Parameters["@Offset"].Value = offset;
                            writeCommand.Parameters["@Length"].Value = length;
                            writeCommand.ExecuteNonQuery();
                            offset += BUFFERSIZE;
                        }
                    }
                }
            }
        }
        private SqlProcedure CreateWriteCommand(IndexBackup backup)
        {
            var sql = String.Format("UPDATE {0} SET [BackupFile].WRITE(@Buffer, @Offset, @Length) WHERE BackupNumber = @BackupNumber", backup.TableName);
            var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text };
            cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@BackupNumber", SqlDbType.Int)).Value = backup.BackupNumber;
            cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@Offset", SqlDbType.BigInt));
            cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@Length", SqlDbType.BigInt));
            cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@Buffer", SqlDbType.VarBinary));
            return cmd;
        }
        private void InitializeNewStream(IndexBackup backup)
        {
            var sql = String.Format("UPDATE {0} SET [BackupFile] = @InitialStream WHERE BackupNumber = @BackupNumber", backup.TableName);
            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.Add(new SqlParameter("@BackupNumber", SqlDbType.Int));
                cmd.Parameters["@BackupNumber"].Value = backup.BackupNumber;
                cmd.Parameters.Add(new SqlParameter("@InitialStream", SqlDbType.VarBinary));
                cmd.Parameters["@InitialStream"].Value = new byte[0];
                cmd.ExecuteNonQuery();
            }
        }
        protected internal override void SetActiveBackup(IndexBackup backup, IndexBackup lastBackup)
        {
            var sql = (lastBackup == null) ?
                String.Format("UPDATE {0} SET IsActive = 1 WHERE BackupNumber = @ActiveBackupNumber", backup.TableName)
                :
                String.Format(@"UPDATE {0} SET IsActive = 1 WHERE BackupNumber = @ActiveBackupNumber
                    UPDATE {1} SET IsActive = 0 WHERE BackupNumber = @InactiveBackupNumber", backup.TableName, lastBackup.TableName);
            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                cmd.Parameters.Add(new SqlParameter("@ActiveBackupNumber", SqlDbType.Int)).Value = backup.BackupNumber;
                if (lastBackup != null)
                    cmd.Parameters.Add(new SqlParameter("@InactiveBackupNumber", SqlDbType.Int)).Value = lastBackup.BackupNumber;
                cmd.ExecuteNonQuery();
            }
        }
        protected override void KeepOnlyLastIndexBackup()
        {
            var backup = LoadLastBackup();
            if (backup == null)
                return;

            backup = new IndexBackup { BackupNumber = backup.BackupNumber - 1 };
            var sql = "TRUNCATE TABLE " + backup.TableName;
            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
                cmd.ExecuteNonQuery();
        }

        protected override Guid GetLastIndexBackupNumber()
        {
            var backup = LoadLastBackup();
            if (backup == null)
                throw GetNoBackupException();
            return backup.RowGuid;
        }
        private Exception GetNoBackupException()
        {
            return new InvalidOperationException("Last index backup does not exist in the database.");
        }

        /*------------------------------------------------------*/

        protected override IndexBackup RecoverIndexBackup(string backupFilePath)
        {
            var backup = LoadLastBackup();
            if (backup == null)
                throw GetNoBackupException();

            if (File.Exists(backupFilePath))
                File.Delete(backupFilePath);

            var dbFileLength = backup.BackupFileLength;

            using (var readCommand = CreateReadCommand(backup))
            {
                using (var stream = new FileStream(backupFilePath, FileMode.Create))
                {
                    BinaryWriter writer = new BinaryWriter(stream);
                    var offset = 0L;
                    while (offset < dbFileLength)
                    {
                        var remnant = dbFileLength - offset;
                        var length = remnant < BUFFERSIZE ? Convert.ToInt32(remnant) : BUFFERSIZE;
                        readCommand.Parameters["@Offset"].Value = offset;
                        readCommand.Parameters["@Length"].Value = length;
                        readCommand.ExecuteNonQuery();
                        var buffer = (byte[])readCommand.ExecuteScalar();
                        writer.Write(buffer, 0, buffer.Length);
                        offset += BUFFERSIZE;
                    }
                }
            }
            return backup;
        }
        private IDataProcedure CreateReadCommand(IndexBackup backup)
        {
            var sql = String.Format("SELECT SUBSTRING([BackupFile], @Offset, @Length) FROM {0} WHERE BackupNumber = @BackupNumber", backup.TableName);
            var cmd = new SqlProcedure { CommandText = sql, CommandType = System.Data.CommandType.Text };
            cmd.CommandType = CommandType.Text;
            cmd.Parameters.Add(new SqlParameter("@BackupNumber", SqlDbType.Int)).Value = backup.BackupNumber;
            cmd.Parameters.Add(new SqlParameter("@Offset", SqlDbType.BigInt));
            cmd.Parameters.Add(new SqlParameter("@Length", SqlDbType.BigInt));
            return cmd;
        }

        /*====================================================== Indexing activity operations */

        // performance:
        // in case of add or update document activity inner join ensures that only the existing contents will be indexed.
        private const string LoadIndexingActivitiesScript_OLD = @"SELECT TOP (@Top) * FROM (
SELECT IndexingActivityId, ActivityType, CreationDate, NodeId, VersionId, SingleVersion, MoveOrRename, IsLastDraftValue, [Path], [Hash], [Extension],
    null IndexDocument, null NodeTypeId, null ParentNodeId, null IsSystem,
	null LastMinorVersionId, null LastMajorVersionId, null Status, null NodeTimestamp, null VersionTimestamp
	FROM IndexingActivity
	WHERE ActivityType IN ('RemoveDocument', 'AddTree', 'RemoveTree')
UNION ALL
SELECT I.IndexingActivityId, I.ActivityType, I.CreationDate, I.NodeId, I.VersionId, I.SingleVersion, I.MoveOrRename, I.IsLastDraftValue, I.[Path] COLLATE Latin1_General_CI_AS, I.[Hash], [Extension],
	V.IndexDocument, N.NodeTypeId, N.ParentNodeId, N.IsSystem,
	N.LastMinorVersionId, N.LastMajorVersionId, V.Status, N.Timestamp NodeTimestamp, V.Timestamp VersionTimestamp
	FROM IndexingActivity I
		JOIN Versions V ON V.VersionId = I.VersionId
		JOIN Nodes N on N.NodeId = V.NodeId
) AS x
{0}
ORDER BY IndexingActivityId
";
        private const string LoadIndexingActivitiesScript = @"SELECT TOP(@Top) I.IndexingActivityId, I.ActivityType, I.CreationDate, I.NodeId, I.VersionId, I.SingleVersion, I.MoveOrRename, I.IsLastDraftValue, I.[Path] COLLATE Latin1_General_CI_AS AS Path, I.[Hash], [Extension],
	V.IndexDocument, N.NodeTypeId, N.ParentNodeId, N.IsSystem,
	N.LastMinorVersionId, N.LastMajorVersionId, V.Status, N.Timestamp NodeTimestamp, V.Timestamp VersionTimestamp
	FROM IndexingActivity I
		LEFT OUTER JOIN Versions V ON V.VersionId = I.VersionId
		LEFT OUTER JOIN Nodes N on N.NodeId = V.NodeId
{0}
ORDER BY IndexingActivityId
";

        public override IIndexingActivity[] LoadIndexingActivities(int fromId, int toId, int count, bool executingUnprocessedActivities, IIndexingActivityFactory activityFactory)
        {
            var sql = String.Format(LoadIndexingActivitiesScript, "WHERE IndexingActivityId >= @From AND IndexingActivityId <= @To");

            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                cmd.Parameters.Add("@From", SqlDbType.Int).Value = fromId;
                cmd.Parameters.Add("@To", SqlDbType.Int).Value = toId;
                cmd.Parameters.Add("@Top", SqlDbType.Int).Value = count;
                return LoadIndexingActivities(cmd, executingUnprocessedActivities, activityFactory);
            }
        }
        public override IIndexingActivity[] LoadIndexingActivities(int[] gaps, bool executingUnprocessedActivities, IIndexingActivityFactory activityFactory)
        {
            var sql = String.Format(LoadIndexingActivitiesScript, String.Format("WHERE IndexingActivityId IN ({0})", string.Join(",", gaps)));

            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                cmd.Parameters.Add("@Top", SqlDbType.Int).Value = gaps.Length;
                return LoadIndexingActivities(cmd, executingUnprocessedActivities, activityFactory);
            }
        }
        private IIndexingActivity[] LoadIndexingActivities(SqlProcedure cmd, bool executingUnprocessedActivities, IIndexingActivityFactory activityFactory)
        {
            var result = new List<IIndexingActivity>();

            using (var reader = cmd.ExecuteReader())
            {
                var indexingActivityIdColumn = reader.GetOrdinal("IndexingActivityId");
                var activityTypeColumn = reader.GetOrdinal("ActivityType");
                var creationDateColumn = reader.GetOrdinal("CreationDate");          // not used
                var nodeIdColumn = reader.GetOrdinal("NodeId");
                var versionIdColumn = reader.GetOrdinal("VersionId");
                var singleVersionColumn = reader.GetOrdinal("SingleVersion");
                var moveOrRenameColumn = reader.GetOrdinal("MoveOrRename");
                var isLastDraftValueColumn = reader.GetOrdinal("IsLastDraftValue");  // not used
                var pathColumn = reader.GetOrdinal("Path");
                var versionTimestampColumn = reader.GetOrdinal("VersionTimestamp");
                var hashColumn = reader.GetOrdinal("Hash");                          // not used
                var indexDocumentColumn = reader.GetOrdinal("IndexDocument");
                var nodeTypeIdColumn = reader.GetOrdinal("NodeTypeId");
                var parentNodeIdColumn = reader.GetOrdinal("ParentNodeId");
                var isSystemColumn = reader.GetOrdinal("IsSystem");
                var lastMinorVersionIdColumn = reader.GetOrdinal("LastMinorVersionId");
                var lastMajorVersionIdColumn = reader.GetOrdinal("LastMajorVersionId");
                var statusColumn = reader.GetOrdinal("Status");
                var nodeTimestampColumn = reader.GetOrdinal("NodeTimestamp");
                var extensionColumn = reader.GetOrdinal("Extension");

                while (reader.Read())
                {
                    var type = (IndexingActivityType)Enum.Parse(typeof(IndexingActivityType), reader.GetSafeString(activityTypeColumn));
                    var activity = activityFactory.CreateActivity(type);
                    activity.Id = reader.GetSafeInt32(indexingActivityIdColumn);
                    activity.ActivityType = type;
                    activity.NodeId = reader.GetSafeInt32(nodeIdColumn);
                    activity.VersionId = reader.GetSafeInt32(versionIdColumn);
                    activity.SingleVersion = reader.GetSafeBooleanFromBoolean(singleVersionColumn);
                    activity.MoveOrRename = reader.GetSafeBooleanFromBoolean(moveOrRenameColumn);
                    activity.Path = reader.GetSafeString(pathColumn) as string;
                    activity.FromDatabase = true;
                    activity.IsUnprocessedActivity = executingUnprocessedActivities;
                    activity.Extension = reader.GetSafeString(extensionColumn);

                    var nodeTypeId = reader.GetSafeInt32(nodeTypeIdColumn);
                    var parentNodeId = reader.GetSafeInt32(parentNodeIdColumn);
                    var isSystem = reader.GetSafeBooleanFromByte(isSystemColumn);
                    var lastMinorVersionId = reader.GetSafeInt32(lastMinorVersionIdColumn);
                    var lastMajorVersionId = reader.GetSafeInt32(lastMajorVersionIdColumn);
                    var status = reader.GetSafeInt16(statusColumn);
                    var nodeTimeStamp = reader.GetSafeLongFromBytes(nodeTimestampColumn);
                    var versionTimestamp = reader.GetSafeLongFromBytes(versionTimestampColumn);

                    var approved = status == (int)VersionStatus.Approved;
                    var isLastMajor = lastMajorVersionId == activity.VersionId;

                    var indexDocumentBytes = reader.IsDBNull(indexDocumentColumn) ? new byte[0] : (byte[])reader.GetValue(indexDocumentColumn);
                    if (indexDocumentBytes != null)
                    {
                        activity.IndexDocumentData = new IndexDocumentData(null, indexDocumentBytes)
                        {
                            NodeTypeId = nodeTypeId,
                            VersionId = activity.VersionId,
                            NodeId = activity.NodeId,
                            ParentId = parentNodeId,
                            Path = activity.Path,
                            IsSystem = isSystem,
                            IsLastDraft = lastMinorVersionId == activity.VersionId,
                            IsLastPublic = approved && isLastMajor,
                            NodeTimestamp = nodeTimeStamp,
                            VersionTimestamp = versionTimestamp,
                        };
                    }
                    result.Add(activity);
                }
            }
            return result.ToArray();
        }

        private const string INSERTINDEXINGACTIVITYSCRIPT = @"INSERT INTO [IndexingActivity]
                ([ActivityType], [CreationDate], [NodeId], [VersionId], [SingleVersion], [MoveOrRename], [IsLastDraftValue], [Path], [VersionTimestamp], [Hash], [Extension]) VALUES
                (@ActivityType, @CreationDate, @NodeId, @VersionId, @SingleVersion, @MoveOrRename, @IsLastDraftValue, @Path, @VersionTimestamp, @Hash, @Extension)
            SELECT @@IDENTITY";

        public override void RegisterIndexingActivity(IIndexingActivity activity)
        {
            using (var cmd = new SqlProcedure { CommandText = INSERTINDEXINGACTIVITYSCRIPT, CommandType = CommandType.Text })
            {
                cmd.Parameters.Add("@ActivityType", SqlDbType.NVarChar, 50).Value = activity.ActivityType.ToString();
                cmd.Parameters.Add("@CreationDate", SqlDbType.DateTime).Value = DateTime.UtcNow;
                cmd.Parameters.Add("@NodeId", SqlDbType.Int).Value = activity.NodeId;
                cmd.Parameters.Add("@VersionId", SqlDbType.Int).Value = activity.VersionId;
                cmd.Parameters.Add("@SingleVersion", SqlDbType.Bit).Value = activity.SingleVersion == null ? (object)DBNull.Value : (object)(activity.SingleVersion.Value ? 1 : 0);
                cmd.Parameters.Add("@MoveOrRename", SqlDbType.Bit).Value = activity.MoveOrRename == null ? (object)DBNull.Value : (object)(activity.MoveOrRename.Value ? 1 : 0);
                cmd.Parameters.Add("@IsLastDraftValue", SqlDbType.Bit).Value = activity.IsLastDraftValue == null ? (object)DBNull.Value : (object)(activity.IsLastDraftValue.Value ? 1 : 0);
                cmd.Parameters.Add("@Path", SqlDbType.NVarChar, 450).Value = (object)activity.Path ?? DBNull.Value;
                cmd.Parameters.Add("@VersionTimestamp", SqlDbType.BigInt).Value = (object)activity.VersionTimestamp ?? DBNull.Value;
                cmd.Parameters.Add("@Hash", SqlDbType.VarBinary, 50).Value = DBNull.Value;
                cmd.Parameters.Add("@Extension", SqlDbType.VarChar, -1).Value = (object)activity.Extension ?? DBNull.Value;

                var id = Convert.ToInt32(cmd.ExecuteScalar());
                activity.Id = id;
            }
        }

        public override void DeleteAllIndexingActivities()
        {
            using (var cmd = new SqlProcedure { CommandText = "DELETE FROM IndexingActivity", CommandType = CommandType.Text })
                cmd.ExecuteNonQuery();
        }

        private const string GETLASTACTIVITYIDSCRIPT = "SELECT CASE WHEN i.last_value IS NULL THEN 0 ELSE CONVERT(int, i.last_value) END last_value FROM sys.identity_columns i JOIN sys.tables t ON i.object_id = t.object_id WHERE t.name = 'IndexingActivity'";
        public override int GetLastActivityId()
        {
            using (var cmd = new SqlProcedure { CommandText = GETLASTACTIVITYIDSCRIPT, CommandType = CommandType.Text })
            {
                var x = cmd.ExecuteScalar();
                if (x == DBNull.Value)
                    return 0;
                return Convert.ToInt32(x);
            }
        }

        // ====================================================== Checking  index integrity

        public override IDataProcedure GetTimestampDataForOneNodeIntegrityCheck(string path, int[] excludedNodeTypeIds)
        {
            string checkNodeSql = "SELECT N.NodeId, V.VersionId, CONVERT(bigint, n.timestamp) NodeTimestamp, CONVERT(bigint, v.timestamp) VersionTimestamp, N.LastMajorVersionId, N.LastMinorVersionId from Versions V join Nodes N on V.NodeId = N.NodeId WHERE N.Path = '{0}' COLLATE Latin1_General_CI_AS";
            if (excludedNodeTypeIds != null && excludedNodeTypeIds.Length > 0)
                checkNodeSql += string.Format(" AND N.NodeTypeId NOT IN ({0})", string.Join(", ", excludedNodeTypeIds));

            var sql = String.Format(checkNodeSql, path);
            var proc = SenseNet.ContentRepository.Storage.Data.DataProvider.CreateDataProcedure(sql);
            proc.CommandType = System.Data.CommandType.Text;
            return proc;
        }
        public override IDataProcedure GetTimestampDataForRecursiveIntegrityCheck(string path, int[] excludedNodeTypeIds)
        {
            string typeFilter = excludedNodeTypeIds != null && excludedNodeTypeIds.Length > 0
                ? string.Format("N.NodeTypeId NOT IN ({0})", string.Join(", ", excludedNodeTypeIds))
                : null;

            string sql;
            if (path == null)
            {
                sql = "SELECT N.NodeId, V.VersionId, CONVERT(bigint, n.timestamp) NodeTimestamp, CONVERT(bigint, v.timestamp) VersionTimestamp, N.LastMajorVersionId, N.LastMinorVersionId from Versions V join Nodes N on V.NodeId = N.NodeId";
                if (!string.IsNullOrEmpty(typeFilter))
                    sql += " WHERE " + typeFilter;
            }
            else
            {
                sql = string.Format("SELECT N.NodeId, V.VersionId, CONVERT(bigint, n.timestamp) NodeTimestamp, CONVERT(bigint, v.timestamp) VersionTimestamp, N.LastMajorVersionId, N.LastMinorVersionId from Versions V join Nodes N on V.NodeId = N.NodeId WHERE (N.Path = '{0}' COLLATE Latin1_General_CI_AS OR N.Path LIKE REPLACE('{0}', '_', '[_]') + '/%' COLLATE Latin1_General_CI_AS)", path);
                if (!string.IsNullOrEmpty(typeFilter))
                    sql += " AND " + typeFilter;
            }

            var proc = CreateDataProcedure(sql);
            proc.CommandType = CommandType.Text;
            return proc;
        }

        // ====================================================== Database backup / restore operations

        private string _databaseName;
        public override string DatabaseName
        {
            get
            {
                if (_databaseName == null)
                {
                    var cnstr = new SqlConnectionStringBuilder(ConnectionStrings.ConnectionString);
                    _databaseName = cnstr.InitialCatalog;
                }
                return _databaseName;
            }
        }

        public override IEnumerable<string> GetScriptsForDatabaseBackup()
        {
            return new[]
            {
                "USE [Master]",
                @"BACKUP DATABASE [{DatabaseName}] TO DISK = N'{BackupFilePath}' WITH NOFORMAT, INIT, NAME = N'SenseNetContentRepository-Full Database Backup', SKIP, NOREWIND, NOUNLOAD, STATS = 10"
            };
        }

        #region // ====================================================== Packaging: IPackageStorageProvider

        public override IDataProcedureFactory DataProcedureFactory { get; set; } = new BuiltInSqlProcedureFactory();

        #region SQL InstalledComponentsScript
        private static readonly string InstalledComponentsScript = @"SELECT P2.Description, P1.ComponentId, P1.ComponentVersion, P1a.ComponentVersion AcceptableVersion
FROM (SELECT ComponentId, MAX(ComponentVersion) ComponentVersion FROM Packages WHERE ComponentId IS NOT NULL GROUP BY ComponentId) P1
JOIN (SELECT ComponentId, MAX(ComponentVersion) ComponentVersion FROM Packages WHERE ComponentId IS NOT NULL 
    AND ExecutionResult != '" + ExecutionResult.Faulty.ToString() + @"'
    AND ExecutionResult != '" + ExecutionResult.Unfinished.ToString() + @"' GROUP BY ComponentId, ExecutionResult) P1a
ON P1.ComponentId = P1a.ComponentId
JOIN (SELECT Description, ComponentId FROM Packages WHERE PackageType = '" + PackageType.Install.ToString() + @"'
    AND ExecutionResult != '" + ExecutionResult.Faulty.ToString() + @"'
    AND ExecutionResult != '" + ExecutionResult.Unfinished.ToString() + @"') P2
ON P1.ComponentId = P2.ComponentId";
        #endregion
        public override IEnumerable<ComponentInfo> LoadInstalledComponents()
        {
            var components = new List<ComponentInfo>();
            using (var cmd = DataProcedureFactory.CreateProcedure())
            {
                cmd.CommandText = InstalledComponentsScript;
                cmd.CommandType = CommandType.Text;
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        components.Add(new ComponentInfo
                        {
                            ComponentId = reader.GetSafeString(reader.GetOrdinal("ComponentId")),                                         // varchar  50   null
                            Version = DecodePackageVersion(reader.GetSafeString(reader.GetOrdinal("ComponentVersion"))),                  // varchar  50   null
                            AcceptableVersion = DecodePackageVersion(reader.GetSafeString(reader.GetOrdinal("AcceptableVersion"))), // varchar  50   null
                            Description = reader.GetSafeString(reader.GetOrdinal("Description")),                                   // nvarchar 1000 null
                        });
                    }
                }
            }
            return components;
        }

        public override IEnumerable<Package> LoadInstalledPackages()
        {
            var packages = new List<Package>();
            using (var cmd = DataProcedureFactory.CreateProcedure())
            {
                cmd.CommandText = "SELECT * FROM Packages";
                cmd.CommandType = CommandType.Text;
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        packages.Add(new Package
                        {
                            Id = reader.GetInt32(reader.GetOrdinal("Id")),                                                                                  // int           not null
                            Description = reader.GetSafeString(reader.GetOrdinal("Description")),                                                           // nvarchar 1000 null
                            ComponentId = reader.GetSafeString(reader.GetOrdinal("ComponentId")),                                                                       // varchar 50    null
                            PackageType = (PackageType)Enum.Parse(typeof(PackageType), reader.GetString(reader.GetOrdinal("PackageType"))),             // varchar 50    not null
                            ReleaseDate = reader.GetDateTimeUtc(reader.GetOrdinal("ReleaseDate")),                                                             // datetime      not null
                            ExecutionDate = reader.GetDateTimeUtc(reader.GetOrdinal("ExecutionDate")),                                                         // datetime      not null
                            ExecutionResult = (ExecutionResult)Enum.Parse(typeof(ExecutionResult), reader.GetString(reader.GetOrdinal("ExecutionResult"))), // varchar 50    not null
                            ExecutionError = DeserializeExecutionError(reader.GetSafeString(reader.GetOrdinal("ExecutionError"))),
                            ComponentVersion = DecodePackageVersion(reader.GetSafeString(reader.GetOrdinal("ComponentVersion"))),                                                                      // varchar 50    null
                        });
                    }
                }
            }
            return packages;
        }
        private Version GetSafeVersion(SqlDataReader reader, string columnName)
        {
            var version = reader.GetSafeString(reader.GetOrdinal(columnName));
            if (version == null)
                return null;
            return Version.Parse(version);
        }

        #region SQL SavePackageScript
        private static readonly string SavePackageScript = @"INSERT INTO Packages
    (  Description,  ComponentId,  PackageType,  ReleaseDate,  ExecutionDate,  ExecutionResult,  ExecutionError,  ComponentVersion,  Manifest) VALUES
    ( @Description, @ComponentId, @PackageType, @ReleaseDate, @ExecutionDate, @ExecutionResult, @ExecutionError, @ComponentVersion, @Manifest)
SELECT @@IDENTITY";
        #endregion
        public override void SavePackage(Package package)
        {
            using (var cmd = DataProcedureFactory.CreateProcedure())
            {
                cmd.CommandText = SavePackageScript;
                cmd.CommandType = CommandType.Text;

                AddParameter(cmd, "@Description", SqlDbType.NVarChar, 1000).Value = (object)package.Description ?? DBNull.Value;
                AddParameter(cmd, "@ComponentId", SqlDbType.VarChar, 50).Value = (object)package.ComponentId ?? DBNull.Value;
                AddParameter(cmd, "@PackageType", SqlDbType.VarChar, 50).Value = package.PackageType.ToString();
                AddParameter(cmd, "@ReleaseDate", SqlDbType.DateTime).Value = package.ReleaseDate;
                AddParameter(cmd, "@ExecutionDate", SqlDbType.DateTime).Value = package.ExecutionDate;
                AddParameter(cmd, "@ExecutionResult", SqlDbType.VarChar, 50).Value = package.ExecutionResult.ToString();
                AddParameter(cmd, "@ExecutionError", SqlDbType.NVarChar).Value = SerializeExecutionError(package.ExecutionError) ?? (object)DBNull.Value;
                AddParameter(cmd, "@ComponentVersion", SqlDbType.VarChar, 50).Value = package.ComponentVersion == null ? DBNull.Value : (object)EncodePackageVersion(package.ComponentVersion);
                AddParameter(cmd, "@Manifest", SqlDbType.NVarChar).Value = package.Manifest ?? (object)DBNull.Value;

                var result = cmd.ExecuteScalar();
                package.Id = Convert.ToInt32(result);
            }
        }

        #region SQL UpdatePackageScript
        private static readonly string UpdatePackageScript = @"UPDATE Packages
    SET ComponentId = @ComponentId,
        Description = @Description,
        PackageType = @PackageType,
        ReleaseDate = @ReleaseDate,
        ExecutionDate = @ExecutionDate,
        ExecutionResult = @ExecutionResult,
        ExecutionError = @ExecutionError,
        ComponentVersion = @ComponentVersion
WHERE Id = @Id
";
        #endregion
        public override void UpdatePackage(Package package)
        {
            using (var cmd = DataProcedureFactory.CreateProcedure())
            {
                cmd.CommandText = UpdatePackageScript;
                cmd.CommandType = CommandType.Text;

                AddParameter(cmd, "@Id", SqlDbType.Int).Value = package.Id;
                AddParameter(cmd, "@Description", SqlDbType.NVarChar, 1000).Value = (object)package.Description ?? DBNull.Value;
                AddParameter(cmd, "@ComponentId", SqlDbType.VarChar, 50).Value = (object)package.ComponentId ?? DBNull.Value;
                AddParameter(cmd, "@PackageType", SqlDbType.VarChar, 50).Value = package.PackageType.ToString();
                AddParameter(cmd, "@ReleaseDate", SqlDbType.DateTime).Value = package.ReleaseDate;
                AddParameter(cmd, "@ExecutionDate", SqlDbType.DateTime).Value = package.ExecutionDate;
                AddParameter(cmd, "@ExecutionResult", SqlDbType.VarChar, 50).Value = package.ExecutionResult.ToString();
                AddParameter(cmd, "@ExecutionError", SqlDbType.NVarChar).Value = SerializeExecutionError(package.ExecutionError) ?? (object)DBNull.Value;
                AddParameter(cmd, "@ComponentVersion", SqlDbType.VarChar, 50).Value = package.ComponentVersion == null ? DBNull.Value : (object)EncodePackageVersion(package.ComponentVersion);

                cmd.ExecuteNonQuery();
            }
        }

        private string SerializeExecutionError(Exception e)
        {
            if (e == null)
                return null;

            var serializer = new JsonSerializer();
            serializer.NullValueHandling = NullValueHandling.Ignore;
            try
            {
                using (var sw = new StringWriter())
                {
                    using (JsonWriter writer = new JsonTextWriter(sw))
                        serializer.Serialize(writer, e);
                    return sw.GetStringBuilder().ToString();
                }
            }
            catch (Exception ee)
            {
                using (var sw = new StringWriter())
                {
                    using (JsonWriter writer = new JsonTextWriter(sw))
                        serializer.Serialize(writer, new Exception("Cannot serialize the execution error: " + ee.Message));
                    return sw.GetStringBuilder().ToString();
                }
            }
        }
        private Exception DeserializeExecutionError(string data)
        {
            if (data == null)
                return null;

            var serializer = new JsonSerializer();
            using (var jreader = new JsonTextReader(new StringReader(data)))
                return serializer.Deserialize<Exception>(jreader);
        }

        #region SQL PackageExistenceScript
        private static readonly string PackageExistenceScript = @"SELECT COUNT(0) FROM Packages
WHERE ComponentId = @ComponentId AND PackageType = @PackageType AND ComponentVersion = @Version
";
        #endregion
        public override bool IsPackageExist(string componentId, PackageType packageType, Version version)
        {
            int count;
            using (var cmd = DataProcedureFactory.CreateProcedure())
            {
                cmd.CommandText = PackageExistenceScript;
                cmd.CommandType = CommandType.Text;

                AddParameter(cmd, "@ComponentId", SqlDbType.VarChar, 50).Value = (object)componentId ?? DBNull.Value;
                AddParameter(cmd, "@PackageType", SqlDbType.VarChar, 50).Value = packageType.ToString();
                AddParameter(cmd, "@Version", SqlDbType.VarChar, 50).Value = EncodePackageVersion(version);
                count = (int)cmd.ExecuteScalar();
            }
            return count > 0;
        }

        public override void DeletePackage(Package package)
        {
            if (package.Id < 1)
                throw new ApplicationException("Cannot delete unsaved package");

            using (var cmd = DataProcedureFactory.CreateProcedure())
            {
                cmd.CommandText = "DELETE FROM Packages WHERE Id = @Id";
                cmd.CommandType = CommandType.Text;

                AddParameter(cmd, "@Id", SqlDbType.Int).Value = package.Id;
                cmd.ExecuteNonQuery();
            }
        }

        public override void DeleteAllPackages()
        {
            using (var cmd = DataProcedureFactory.CreateProcedure())
            {
                cmd.CommandText = "TRUNCATE TABLE Packages";
                cmd.CommandType = CommandType.Text;
                cmd.ExecuteNonQuery();
            }
        }

        #region SQL LoadManifestScript
        private static readonly string LoadManifestScript = @"SELECT Manifest FROM Packages WHERE Id = @Id";
        #endregion
        public override void LoadManifest(Package package)
        {
            using (var cmd = DataProcedureFactory.CreateProcedure())
            {
                cmd.CommandText = LoadManifestScript;
                cmd.CommandType = CommandType.Text;

                AddParameter(cmd, "@Id", SqlDbType.Int).Value = package.Id;

                var value = cmd.ExecuteScalar();
                package.Manifest = (string)(value == DBNull.Value ? null : value);
            }
        }

        private static string EncodePackageVersion(Version v)
        {
            if (v.Build < 0)
                return String.Format("{0:0#########}.{1:0#########}", v.Major, v.Minor);
            if (v.Revision < 0)
                return String.Format("{0:0#########}.{1:0#########}.{2:0#########}", v.Major, v.Minor, v.Build);
            return String.Format("{0:0#########}.{1:0#########}.{2:0#########}.{3:0#########}", v.Major, v.Minor, v.Build, v.Revision);
        }
        private static Version DecodePackageVersion(string s)
        {
            if (s == null)
                return null;
            return Version.Parse(s);
        }

        private IDataParameter AddParameter(IDataProcedure proc, string name, SqlDbType dbType)
        {
            var p = new SqlParameter(name, dbType);
            proc.Parameters.Add(p);
            return p;
        }
        private IDataParameter AddParameter(IDataProcedure proc, string name, SqlDbType dbType, int size)
        {
            var p = new SqlParameter(name, dbType, size);
            proc.Parameters.Add(p);
            return p;
        }

        #endregion

        #region // ====================================================== Tree lock

        protected internal override int AcquireTreeLock(string path)
        {
            var parentChain = GetParentChain(path);

            var sql = @"
BEGIN TRAN
IF NOT EXISTS (
	    SELECT TreeLockId FROM TreeLocks
	    WHERE @TimeMin < LockedAt AND (
			[Path] LIKE (REPLACE(@Path0, '_', '[_]') + '/%') OR
			[Path] IN ( "
                + String.Join(", ", Enumerable.Range(0, parentChain.Length).Select(i => "@Path" + i))
                + @" ) ) )
    INSERT INTO TreeLocks ([Path] ,[LockedAt])
	    OUTPUT INSERTED.TreeLockId
	    VALUES (@Path0, GETDATE())
COMMIT";

            var result = 0;
            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                cmd.Parameters.Add(new SqlParameter("@TimeMin", SqlDbType.DateTime)).Value = GetObsoleteLimitTime();
                for (int i = 0; i < parentChain.Length; i++)
                    cmd.Parameters.Add(new SqlParameter("@Path" + i, SqlDbType.NVarChar, 450)).Value = parentChain[i];
                var resultObject = cmd.ExecuteScalar();
                result = (resultObject == null || resultObject == DBNull.Value) ? 0 : (int)resultObject;
            }

            return result;
        }
        protected internal override bool IsTreeLocked(string path)
        {
            var parentChain = GetParentChain(path);

            var sql = @"SELECT TreeLockId FROM TreeLocks
                WHERE  @TimeMin < LockedAt AND (
			        [Path] LIKE (REPLACE(@Path0, '_', '[_]') + '/%') OR
	                [Path] IN ( "
                        + String.Join(", ", Enumerable.Range(0, parentChain.Length).Select(i => "@Path" + i))
                        + @" ) )";

            var locked = false;
            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                cmd.Parameters.Add(new SqlParameter("@TimeMin", SqlDbType.DateTime)).Value = GetObsoleteLimitTime();
                for (int i = 0; i < parentChain.Length; i++)
                    cmd.Parameters.Add(new SqlParameter("@Path" + i, SqlDbType.NVarChar, 450)).Value = parentChain[i];
                var resultObject = cmd.ExecuteScalar();
                locked = resultObject != null && resultObject != DBNull.Value;
            }

            return locked;
        }
        protected internal override void ReleaseTreeLock(int[] lockIds)
        {
            var sql = String.Format("DELETE FROM TreeLocks WHERE TreeLockId IN ({0})",
                String.Join(", ", Enumerable.Range(0, lockIds.Length).Select(i => "@Id" + i)));
            var index = 0;
            var prms = lockIds.Select(i => new SqlParameter("@Id" + index++, i)).ToArray();
            index = 0;
            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                cmd.Parameters.AddRange(lockIds.Select(i => new SqlParameter("@Id" + index++, i)).ToArray());
                cmd.ExecuteNonQuery();
            }
            DeleteUnusedLocks();
        }
        protected internal override Dictionary<int, string> LoadAllTreeLocks()
        {
            var sql = @"SELECT TreeLockId, Path FROM TreeLocks";

            var result = new Dictionary<int, string>();
            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(reader.GetInt32(0), reader.GetString(1));
                    }
                }
            }

            return result;
        }

        private void DeleteUnusedLocks()
        {
            var sql = "DELETE FROM TreeLocks WHERE LockedAt < @TimeMin";
            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                cmd.Parameters.Add(new SqlParameter("@TimeMin", SqlDbType.DateTime)).Value = GetObsoleteLimitTime();
                cmd.ExecuteNonQuery();
            }
        }

        private string[] GetParentChain(string path)
        {
            var paths = path.Split(RepositoryPath.PathSeparatorChars, StringSplitOptions.RemoveEmptyEntries);
            paths[0] = "/" + paths[0];
            for (int i = 1; i < paths.Length; i++)
                paths[i] = paths[i - 1] + "/" + paths[i];
            return paths.Reverse().ToArray();
        }
        private DateTime GetObsoleteLimitTime()
        {
            return DateTime.Now.AddHours(-8.0);
        }

        #endregion

        // ====================================================== Only for tests

        protected override string GetSecurityControlStringForTestsInternal()
        {
            var securityEntitiesArray = new List<object>();
            using (var cmd = new SqlProcedure { CommandText = "SELECT NodeId, ParentNodeId, [OwnerId] FROM Nodes ORDER BY NodeId", CommandType = CommandType.Text })
            {
                using (var reader = cmd.ExecuteReader())
                {
                    int count = 0;
                    while (reader.Read())
                    {
                        securityEntitiesArray.Add(new
                        {
                            NodeId = reader.GetSafeInt32(0),
                            ParentId = reader.GetSafeInt32(1),
                            OwnerId = reader.GetSafeInt32(2),
                        });

                        count++;
                        // it is neccessary to examine the number of Nodes, because loading a too big security structure may require too much resource
                        if (count > 200000)
                            throw new ArgumentOutOfRangeException("number of Nodes");
                    }
                }
            }
            return JsonConvert.SerializeObject(securityEntitiesArray);
        }

        protected override int GetPermissionLogEntriesCountAfterMomentInternal(DateTime moment)
        {
            var count = 0;
            var sql = String.Format("SELECT COUNT(1) FROM LogEntries WHERE Title = 'PermissionChanged' AND LogDate>='{0}'", moment.ToString("yyyy-MM-dd HH:mm:ss"));
            var proc = SenseNet.ContentRepository.Storage.Data.DataProvider.CreateDataProcedure(sql);
            proc.CommandType = System.Data.CommandType.Text;
            using (var reader = proc.ExecuteReader())
            {
                while (reader.Read())
                {
                    count = reader.GetSafeInt32(0);
                }
            }
            return count;
        }

        internal override AuditLogEntry[] LoadLastAuditLogEntries(int count)
        {
            var result = new List<AuditLogEntry>();

            var sql = String.Format("SELECT TOP({0}) * FROM LogEntries ORDER BY LogId DESC", count);

            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new AuditLogEntry
                        {
                            Id = reader.GetInt32(reader.GetOrdinal("LogId")),
                            EventId = reader.GetSafeInt32(reader.GetOrdinal("EventId")),
                            Title = reader.GetSafeString(reader.GetOrdinal("Title")),
                            ContentId = reader.GetSafeInt32(reader.GetOrdinal("ContentId")),
                            ContentPath = reader.GetSafeString(reader.GetOrdinal("ContentPath")),
                            UserName = reader.GetSafeString(reader.GetOrdinal("UserName")),
                            LogDate = reader.GetDateTime(reader.GetOrdinal("LogDate")),
                            Message = reader.GetSafeString(reader.GetOrdinal("Message")),
                            FormattedMessage = reader.GetSafeString(reader.GetOrdinal("FormattedMessage")),
                        });
                    }
                }
            }
            result.Reverse();
            return result.ToArray();
        }

        private static string EscapeForLikeOperator(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            text = text.Replace("[", "[[]").Replace("_", "[_]").Replace("%", "[%]");

            return text;
        }
    }
}
