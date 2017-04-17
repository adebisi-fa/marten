using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Baseline;
using Marten.Events;
using Marten.Schema;
using Marten.Schema.BulkLoading;
using Marten.Schema.Identity;
using Marten.Schema.Identity.Sequences;
using Marten.Transforms;
using Marten.Util;

namespace Marten.Storage
{
    public class Tenant : ITenant
    {
        private readonly ConcurrentDictionary<Type, bool> _checks = new ConcurrentDictionary<Type, bool>();
        private readonly IConnectionFactory _factory;
        private readonly StorageFeatures _features;
        private readonly StoreOptions _options;

        public Tenant(StorageFeatures features, StoreOptions options, IConnectionFactory factory, string tenantId)
        {
            TenantId = tenantId;
            _features = features;
            _options = options;
            _factory = factory;
        }

        public string TenantId { get; }

        public void RemoveSchemaItems(Type featureType, StorageFeatures features)
        {
            var feature = features.FindFeature(featureType);
            var writer = new StringWriter();

            foreach (var schemaObject in feature.Objects)
            {
                schemaObject.WriteDropStatement(_options.DdlRules, writer);
            }

            _factory.RunSql(writer.ToString());
        }

        public void ResetSchemaExistenceChecks()
        {
            _checks.Clear();
        }

        public void EnsureStorageExists(Type featureType)
        {
            if (_options.AutoCreateSchemaObjects == AutoCreate.None) return;

            if (_checks.ContainsKey(featureType)) return;

            // TODO -- ensure the system type here too?
            var feature = _features.FindFeature(featureType);
            if (feature == null)
                throw new ArgumentOutOfRangeException(nameof(featureType),
                    $"Unknown feature type {featureType.FullName}");


            if (_checks.ContainsKey(feature.StorageType))
            {
                _checks[featureType] = true;
                return;
            }

            foreach (var dependentType in feature.DependentTypes())
            {
                EnsureStorageExists(dependentType);
            }

            // TODO -- might need to do a lock here.
            generateOrUpdateFeature(featureType, feature);
        }

        private void generateOrUpdateFeature(Type featureType, IFeatureSchema feature)
        {
            using (var conn = _factory.Create())
            {
                conn.Open();

                var patch = new SchemaPatch(_options.DdlRules);
                patch.Apply(conn, _options.AutoCreateSchemaObjects, feature.Objects);
                patch.AssertPatchingIsValid(_options.AutoCreateSchemaObjects);

                var ddl = patch.UpdateDDL;
                if (patch.Difference != SchemaPatchDifference.None && ddl.IsNotEmpty())
                {
                    var cmd = conn.CreateCommand(ddl);
                    try
                    {
                        cmd.ExecuteNonQuery();
                        _options.Logger().SchemaChange(ddl);
                        _checks[featureType] = true;
                        if (feature.StorageType != featureType)
                        {
                            _checks[feature.StorageType] = true;
                        }
                    }
                    catch (Exception e)
                    {
                        throw new MartenCommandException(cmd, e);
                    }
                }
            }
        }

        public IDocumentStorage StorageFor(Type documentType)
        {
            EnsureStorageExists(documentType);
            return _features.StorageFor(documentType);
        }

        public IDocumentMapping MappingFor(Type documentType)
        {
            EnsureStorageExists(documentType);
            return _features.MappingFor(documentType);
        }

        public ISequences Sequences
        {
            get
            {
                EnsureStorageExists(typeof(SequenceFactory));

                return _features.Sequences;
            }
        }
        public IDocumentStorage<T> StorageFor<T>()
        {
            EnsureStorageExists(typeof(T));
            return _features.StorageFor(typeof(T)).As<IDocumentStorage<T>>();
        }

        public IdAssignment<T> IdAssignmentFor<T>()
        {
            // TODO -- this will have to be tracked per tenant because of the sequences
            EnsureStorageExists(typeof(T));
            return _features.IdAssignmentFor<T>();
        }

        public TransformFunction TransformFor(string name)
        {
            EnsureStorageExists(typeof(DocumentTransforms));
            return _features.Transforms.For(name);
        }

        public IBulkLoader<T> BulkLoaderFor<T>()
        {
            EnsureStorageExists(typeof(T));
            return _features.BulkLoaderFor<T>();
        }

        public EventGraph Events { get; }

        public IEnumerable<IDocumentMapping> AllMappings => _features.AllDocumentMappings;
    }
}