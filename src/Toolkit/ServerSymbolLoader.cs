﻿using Kusto.Cloud.Platform.Data;
using Kusto.Cloud.Platform.Utils;
using Kusto.Data;
using Kusto.Data.Data;
using Kusto.Data.Net.Client;
using Kusto.Language;
using Kusto.Language.Symbols;
using Kusto.Data.Common;
using System.ComponentModel;

namespace Kusto.Toolkit
{
    using static SymbolFacts;

    /// <summary>
    /// A <see cref="SymbolLoader"/> that retrieves schema symbols from a Kusto server.
    /// </summary>
    public class ServerSymbolLoader : SymbolLoader
    {
        private readonly KustoConnectionStringBuilder _defaultConnection;
        private readonly string _defaultClusterName;
        private readonly string _defaultDomain;
        private readonly Dictionary<string, ICslAdminProvider> _dataSourceToAdminProviderMap = new Dictionary<string, ICslAdminProvider>();
        private readonly Dictionary<string, HashSet<string>> _clusterToBadDbNameMap = new Dictionary<string, HashSet<string>>();

        /// <summary>
        /// Creates a new <see cref="SymbolLoader"/> instance.
        /// </summary>
        /// <param name="clusterConnection">The cluster connection.</param>
        /// <param name="defaultDomain">The domain used to convert short cluster host names into full cluster host names.
        /// This string must start with a dot.  If not specified, the default domain is ".Kusto.Windows.Net"
        /// </param>
        public ServerSymbolLoader(KustoConnectionStringBuilder clusterConnection, string defaultDomain = null)
        {
            if (clusterConnection == null)
                throw new ArgumentNullException(nameof(clusterConnection));

            _defaultConnection = clusterConnection;
            _defaultClusterName = GetHost(clusterConnection);
            _defaultDomain = String.IsNullOrEmpty(defaultDomain)
                ? KustoFacts.KustoWindowsNet
                : defaultDomain;
        }

        /// <summary>
        /// Creates a new <see cref="SymbolLoader"/> instance. recommended method: SymbolLoader(KustoConnectionStringBuilder clusterConnection)
        /// </summary>
        /// <param name="clusterConnection">The cluster connection string.</param>
        /// <param name="defaultDomain">The domain used to convert short cluster host names into full cluster host names.
        /// This string must start with a dot.  If not specified, the default domain is ".Kusto.Windows.Net"
        /// </param>
        [Obsolete("Use constructor with KustoConnectionStringBuilder for proper handling of authentication and secrets.")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public ServerSymbolLoader(string clusterConnection, string defaultDomain = null)
            : this(new KustoConnectionStringBuilder(clusterConnection), defaultDomain)
        {
        }

        public override string DefaultDomain => _defaultDomain;
        public override string DefaultCluster => _defaultClusterName;

        /// <summary>
        /// The default database specified in the connection
        /// </summary>
        public string DefaultDatabase => _defaultConnection.InitialCatalog;

        /// <summary>
        /// Dispose any open resources.
        /// </summary>
        public override void Dispose()
        {
            // Disposes any open admin providers.
            var providers = _dataSourceToAdminProviderMap.Values.ToList();
            _dataSourceToAdminProviderMap.Clear();

            foreach (var provider in providers)
            {
                provider.Dispose();
            }
        }

        /// <summary>
        /// Gets or Creates an admin provider instance.
        /// </summary>
        private ICslAdminProvider GetOrCreateAdminProvider(KustoConnectionStringBuilder connection)
        {
            if (!_dataSourceToAdminProviderMap.TryGetValue(connection.DataSource, out var provider))
            {
                provider = KustoClientFactory.CreateCslAdminProvider(connection);
                _dataSourceToAdminProviderMap.Add(connection.DataSource, provider);
            }

            return provider;
        }

        /// <summary>
        /// Loads a list of database names for the specified cluster.
        /// If the cluster name is not specified, the loader's default cluster name is used.
        /// Returns null if the cluster is not found.
        /// </summary>
        public override async Task<IReadOnlyList<DatabaseName>> LoadDatabaseNamesAsync(string? clusterName = null, bool throwOnError = false, CancellationToken cancellationToken = default)
        {
            clusterName = string.IsNullOrEmpty(clusterName)
                ? _defaultClusterName
                : GetFullHostName(clusterName, _defaultDomain);

            var connection = GetClusterConnection(clusterName);
            var provider = GetOrCreateAdminProvider(connection);

            DatabaseNamesResult[] databases = await ExecuteControlCommandAsync<DatabaseNamesResult>(
                provider, database: "",
                ".show databases",
                throwOnError, cancellationToken)
                .ConfigureAwait(false);

            var DatabaseNames = databases
                .Select(d => new DatabaseName(d.DatabaseName, d.PrettyName))
                .ToArray();

            if (DatabaseNames.Length > 0)
            {
                return DatabaseNames;
            }
            return null;
        }

        private bool IsBadDatabaseName(string clusterName, string databaseName)
        {
            return _clusterToBadDbNameMap.TryGetValue(clusterName, out var badDbNames)
                && badDbNames.Contains(databaseName);
        }

        private void AddBadDatabaseName(string clusterName, string databaseName)
        {
            if (!_clusterToBadDbNameMap.TryGetValue(clusterName, out var badDbNames))
            {
                badDbNames = new HashSet<string>();
                _clusterToBadDbNameMap.Add(clusterName, badDbNames);
            }

            badDbNames.Add(databaseName);
        }

        /// <summary>
        /// Loads the corresponding database's schema and returns a new <see cref="DatabaseSymbol"/> initialized from it.
        /// If the cluster name is not specified, the loader's default cluster name is used.
        /// Returns null if the database is not found.
        /// </summary>
        public override async Task<DatabaseSymbol> LoadDatabaseAsync(string databaseName, string? clusterName = null, bool throwOnError = false, CancellationToken cancellationToken = default)
        {
            clusterName = string.IsNullOrEmpty(clusterName)
                ? _defaultClusterName
                : GetFullHostName(clusterName, _defaultDomain);

            // if we've already determined this database name is bad, then bail out
            if (IsBadDatabaseName(clusterName, databaseName))
            {
                if (throwOnError) {
                    throw new InvalidOperationException($"Specified database name '{databaseName}' does not exist in cluster '{clusterName}'");
                }
                return null;
            }

            var connection = GetClusterConnection(clusterName);
            var provider = GetOrCreateAdminProvider(connection);

            var dbName = await GetBothDatabaseNamesAsync(provider, databaseName, throwOnError, cancellationToken).ConfigureAwait(false);
            if (dbName == null)
            {
                AddBadDatabaseName(clusterName, databaseName);
                if (throwOnError)
                {
                    throw new InvalidOperationException($"Specified database name '{databaseName}' does not exist in cluster '{clusterName}'");
                }
                return null;
            }

            var tables = await LoadTablesAsync(provider, dbName.Name, throwOnError, cancellationToken).ConfigureAwait(false);
            var externalTables = await LoadExternalTablesAsync(provider, dbName.Name, throwOnError, cancellationToken).ConfigureAwait(false);
            var materializedViews = await LoadMaterializedViewsAsync(provider, dbName.Name, throwOnError, cancellationToken).ConfigureAwait(false);
            var functions = await LoadFunctionsAsync(provider, dbName.Name, throwOnError, cancellationToken).ConfigureAwait(false);
            var entityGroups = await LoadEntityGroupsAsync(provider, dbName.Name, throwOnError, cancellationToken).ConfigureAwait(false);

            var members = new List<Symbol>();
            members.AddRange(tables);
            members.AddRange(externalTables);
            members.AddRange(materializedViews);
            members.AddRange(functions);
            members.AddRange(entityGroups);

            var databaseSymbol = new DatabaseSymbol(dbName.Name, dbName.PrettyName, members);
            return databaseSymbol;
        }

        /// <summary>
        /// Returns the database name and pretty name given either the database name or the pretty name.
        /// </summary>
        private async Task<DatabaseName> GetBothDatabaseNamesAsync(ICslAdminProvider provider, string databaseNameOrPrettyName, bool throwOnError, CancellationToken cancellationToken)
        {
            DatabaseNamesResult[] dbInfos;
            dbInfos = await ExecuteControlCommandAsync<DatabaseNamesResult>(
                provider,
                database: databaseNameOrPrettyName,
                ".show database identity",
                throwOnError, cancellationToken)
                .ConfigureAwait(false);

            var dbInfo = dbInfos.FirstOrDefault();

            try
            {
                return new DatabaseName(dbInfo.DatabaseName, dbInfo.PrettyName);
            }
            catch (Exception)
            {
                if (throwOnError) throw;
                return null;
            }

        }
        private async Task<IReadOnlyList<TableSymbol>> LoadTablesAsync(ICslAdminProvider provider, string databaseName, bool throwOnError, CancellationToken cancellationToken)
        {
            LoadTablesResultCSL[] tableSchemas;

            // get table schema from .show database xxx schema
            var columnTableDataTypes = await ExecuteControlCommandAsync<LoadTablesResult>(
                provider, databaseName,
                $".show database {KustoFacts.GetBracketedName(databaseName)} schema",
                throwOnError, cancellationToken)
                .ConfigureAwait(false);
            var schemaComponents = columnTableDataTypes
                .Where(line => line.TableName.IsNotNullOrEmpty())
                .GroupBy(line => line.TableName);
            tableSchemas = schemaComponents
                .Select(component => new LoadTablesResultCSL
                {
                    TableName = component.Key,
                    Schema = component.Where(line => line.ColumnName.IsNotNullOrEmpty()).Select(c => $"{c.ColumnName}:{c.ColumnTypeCSL}").StringJoin(", "),
                    DocString = component.FirstOrDefault(c => c.DocString.IsNotNullOrEmpty())?.DocString
                }).ToArray();

            var tables = tableSchemas.Select(schema => new TableSymbol(schema.TableName, "(" + schema.Schema + ")", schema.DocString)).ToList();
            return tables;
        }

        private async Task<IReadOnlyList<TableSymbol>> LoadExternalTablesAsync(ICslAdminProvider provider, string databaseName, bool throwOnError, CancellationToken cancellationToken)
        {
            var tables = new List<TableSymbol>();

            // get external tables from .show external tables and .show external table xxx cslschema
            var externalTables = await ExecuteControlCommandAsync<LoadExternalTablesResult1>(
                provider, databaseName, 
                ".show external tables | project TableName, DocString", 
                throwOnError, cancellationToken);
            if (externalTables != null)
            {
                foreach (var et in externalTables)
                {
                    var etSchemas = await ExecuteControlCommandAsync<LoadExternalTablesResult2>(
                        provider, databaseName,
                        $".show external table {KustoFacts.GetBracketedName(et.TableName)} cslschema | project TableName, Schema",
                        throwOnError, cancellationToken)
                        .ConfigureAwait(false);

                    if (etSchemas != null && etSchemas.Length > 0)
                    {
                        var mvSymbol = new ExternalTableSymbol(et.TableName, "(" + etSchemas[0].Schema + ")", et.DocString);
                        tables.Add(mvSymbol);
                    }
                }
            }

            return tables;
        }

        private async Task<IReadOnlyList<TableSymbol>> LoadMaterializedViewsAsync(ICslAdminProvider provider, string databaseName, bool throwOnError, CancellationToken cancellationToken)
        {
            var tables = new List<TableSymbol>();

            // get materialized views from .show materialized-views and .show materialized-view xxx cslschema
            var materializedViews = await ExecuteControlCommandAsync<LoadMaterializedViewsResult1>(
                provider, databaseName,
                ".show materialized-views | project Name, Query, DocString",
                throwOnError, cancellationToken)
                .ConfigureAwait(false);

            if (materializedViews != null)
            {
                foreach (var mv in materializedViews)
                {
                    var mvSchemas = await ExecuteControlCommandAsync<LoadMeterializedViewsResult2>(
                        provider, databaseName, 
                        $".show materialized-view {KustoFacts.GetBracketedName(mv.Name)} cslschema | project TableName, Schema", 
                        throwOnError, cancellationToken)
                        .ConfigureAwait(false);

                    if (mvSchemas != null && mvSchemas.Length > 0)
                    {
                        var mvSymbol = new MaterializedViewSymbol(mv.Name, "(" + mvSchemas[0].Schema + ")", mv.Query, mv.DocString);
                        tables.Add(mvSymbol);
                    }
                }
            }

            return tables;
        }

        private async Task<IReadOnlyList<FunctionSymbol>> LoadFunctionsAsync(ICslAdminProvider provider, string databaseName, bool throwOnError, CancellationToken cancellationToken)
        {
            var functions = new List<FunctionSymbol>();

            // get functions for .show functions
            var functionSchemas = await ExecuteControlCommandAsync<LoadFunctionsResult>(
                provider, databaseName,
                ".show functions", 
                throwOnError, cancellationToken)
                .ConfigureAwait(false);

            if (functionSchemas == null)
                return functions;

            foreach (var fun in functionSchemas)
            {
                var functionSymbol = new FunctionSymbol(fun.Name, fun.Parameters, fun.Body, fun.DocString);
                functions.Add(functionSymbol);
            }

            return functions;
        }

        private async Task<IReadOnlyList<EntityGroupSymbol>> LoadEntityGroupsAsync(ICslAdminProvider provider, string databaseName, bool throwOnError, CancellationToken cancellationToken)
        {
            var entityGroupSymbols = new List<EntityGroupSymbol>();

            // get entity groups via .show entity_groups
            var entityGroups = await ExecuteControlCommandAsync<LoadEntityGroupsResult>(
                provider, databaseName, 
                ".show entity_groups | project Name, Entities",
                throwOnError, cancellationToken)
                .ConfigureAwait(false);

            if (entityGroups == null)
                return entityGroupSymbols;

            return entityGroups.Select(eg => new EntityGroupSymbol(eg.Name, eg.Entities)).ToList();
        }

        /// <summary>
        /// Executes a query or command against a kusto cluster and returns a sequence of result row instances.
        /// </summary>
        private async Task<T[]> ExecuteControlCommandAsync<T>(ICslAdminProvider provider, string database, string command, bool throwOnError, CancellationToken cancellationToken)
        {
            try
            {
                var resultReader = await provider.ExecuteControlCommandAsync(database, command).ConfigureAwait(false);
                
                var results = KustoDataReaderParser.ParseV1(resultReader, null);
                
                var primaryResults = results.GetMainResultsOrNull();
                if (primaryResults != null)
                {
                    var tableReader = primaryResults.TableData.CreateDataReader();
                    var objectReader = new ObjectReader<T>(tableReader);
                    return objectReader.ToArray();
                }

                return new List<T>().ToArray();
            }
            catch (Exception) when (!throwOnError)
            {
                return new List<T>().ToArray();
            }
        }

        private string GetHost(KustoConnectionStringBuilder connection) =>
            new Uri(connection.DataSource).Host;

        private KustoConnectionStringBuilder GetClusterConnection(string clusterUriOrName)
        {
            if (string.IsNullOrEmpty(clusterUriOrName)
                || clusterUriOrName == _defaultClusterName)
            {
                return _defaultConnection;
            }

            if (string.IsNullOrWhiteSpace(clusterUriOrName))
                return null;

            var clusterUri = clusterUriOrName;

            if (!clusterUri.Contains("://"))
            {
                clusterUri = _defaultConnection.ConnectionScheme + "://" + clusterUri;
            }

            clusterUri = KustoFacts.GetFullHostName(clusterUri, _defaultDomain);

            // borrow most security settings from default cluster connection
            var connection = new KustoConnectionStringBuilder(_defaultConnection);
            connection.DataSource = clusterUri;
            connection.ApplicationCertificateBlob = _defaultConnection.ApplicationCertificateBlob;
            connection.ApplicationKey = _defaultConnection.ApplicationKey;
            connection.InitialCatalog = "NetDefaultDB";

            return connection;
        }

        public class DatabaseNamesResult
        {
            public string DatabaseName = "default";
            public string? PersistentStorage;
            public string? Version;
            public Int16 IsCurrent;
            public string? DatabaseAccessMode;
            public string PrettyName = "default";
            public Int16 CurrentUserIsUnrestrictedViewer;
            public string? DatabaseId;
            public string? InTransitionTo;
            public string? SuspensionState;
        }

        public class LoadTablesResultCSL
        {
            public string TableName;
            public string Schema;
            public string DocString = "";
        }

        public class LoadTablesResult
        {
            public string DatabaseName;
            public string TableName;
            public string ColumnName;
            public string ColumnType;
            public bool IsDefaultTable;
            public bool IsDefaultColumn;
            public string PrettyName;
            public string Version;
            public string Folder;
            public string DocString;

            private static Dictionary<string, string> _typeToCSL = new Dictionary<string, string>()
            {
                { "System.SByte", "bool" },
                { "System.DateTime", "datetime" },
                { "System.Data.SqlTypes.SqlDecimal", "decimal" },
                { "System.Object", "dynamic" },
                { "System.Guid", "guid" },
                { "System.Int32", "int" },
                { "System.Int64", "long" },
                { "System.Double", "real" },
                { "System.String", "string" },
                { "System.TimeSpan", "timespan" }
            };
            public string ColumnTypeCSL => _typeToCSL[ColumnType];
        }

        public class LoadExternalTablesResult1
        {
            public string TableName;
            public string DocString;
        }

        public class LoadExternalTablesResult2
        {
            public string TableName;
            public string Schema;
        }

        public class LoadMaterializedViewsResult1
        {
            public string Name;
            public string Query;
            public string DocString;
        }

        public class LoadMeterializedViewsResult2
        {
            public string Name;
            public string Schema;
        }

        public class LoadFunctionsResult
        {
            public string Name;
            public string Parameters;
            public string Body;
            public string Folder;
            public string DocString;
        }

        public class LoadEntityGroupsResult
        {
            public string Name;
            public string Entities;
        }
    }
}
