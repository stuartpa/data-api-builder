// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable disable
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using System.Net;
using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Config.Converters;
using Azure.DataApiBuilder.Config.DatabasePrimitives;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Core.Configurations;
using Azure.DataApiBuilder.Core.Services;
using Azure.DataApiBuilder.Core.Services.MetadataProviders;
using Azure.DataApiBuilder.Service.Exceptions;
using Azure.DataApiBuilder.Service.Tests.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Azure.DataApiBuilder.Service.Tests.UnitTests
{
    /// <summary>
    /// Test class to perform semantic validations on the runtime config object. At this point,
    /// the tests focus on the permissions portion of the entities property within the RuntimeConfig object.
    /// </summary>
    [TestClass]
    public class ConfigValidationUnitTests
    {
        /// <summary>
        /// Test method to validate that an appropriate exception is thrown when database policy tries to reference a field
        /// which is not accessible.
        /// </summary>
        /// <param name="dbPolicy">Database policy under test.</param>
        [DataTestMethod]
        [DataRow("@claims.id eq @item.id", DisplayName = "Field id is not accessible")]
        [DataRow("@claims.user_email eq @item.email and @claims.user_name ne @item.name", DisplayName = "Field email is not accessible")]
        public void InaccessibleFieldRequestedByPolicy(string dbPolicy)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedCols: new HashSet<string> { "*" },
                excludedCols: new HashSet<string> { "id", "email" },
                databasePolicy: dbPolicy
            );

            RuntimeConfigValidator configValidator = InitializeRuntimeConfigValidator();

            // Assert that expected exception is thrown.
            DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() => configValidator.ValidatePermissionsInConfig(runtimeConfig));
            Assert.AreEqual("Not all the columns required by policy are accessible.", ex.Message);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
            Assert.AreEqual(DataApiBuilderException.SubStatusCodes.ConfigValidationError, ex.SubStatusCode);
        }

        /// <summary>
        /// Test method to validate that only 1 CRUD operation is supported for stored procedure
        /// and every role has that same single operation.
        /// </summary>
        [DataTestMethod]
        [DataRow("anonymous", new string[] { "execute" }, null, null, true, false, DisplayName = "Stored-procedure with valid execute permission only")]
        [DataRow("anonymous", new string[] { "*" }, null, null, true, false, DisplayName = "Stored-procedure with valid wildcard permission only, which resolves to execute")]
        [DataRow("anonymous", new string[] { "execute", "read" }, null, null, false, false, DisplayName = "Invalidly define operation in excess of execute")]
        [DataRow("anonymous", new string[] { "create", "read" }, null, null, false, false, DisplayName = "Stored-procedure with create-read permission")]
        [DataRow("anonymous", new string[] { "update", "read" }, null, null, false, false, DisplayName = "Stored-procedure with update-read permission")]
        [DataRow("anonymous", new string[] { "delete", "read" }, null, null, false, false, DisplayName = "Stored-procedure with delete-read permission")]
        [DataRow("anonymous", new string[] { "create" }, null, null, false, false, DisplayName = "Stored-procedure with invalid create permission")]
        [DataRow("anonymous", new string[] { "read" }, null, null, false, false, DisplayName = "Stored-procedure with invalid read permission")]
        [DataRow("anonymous", new string[] { "update" }, null, null, false, false, DisplayName = "Stored-procedure with invalid update permission")]
        [DataRow("anonymous", new string[] { "delete" }, null, null, false, false, DisplayName = "Stored-procedure with invalid delete permission")]
        [DataRow("anonymous", new string[] { "update", "create" }, null, null, false, false, DisplayName = "Stored-procedure with update-create permission")]
        [DataRow("anonymous", new string[] { "delete", "read", "update" }, null, null, false, false, DisplayName = "Stored-procedure with delete-read-update permission")]
        [DataRow("anonymous", new string[] { "execute" }, "authenticated", new string[] { "execute" }, true, false, DisplayName = "Stored-procedure with valid execute permission on all roles")]
        [DataRow("anonymous", new string[] { "*" }, "authenticated", new string[] { "*" }, true, false, DisplayName = "Stored-procedure with valid wildcard permission on all roles, which resolves to execute")]
        [DataRow("anonymous", new string[] { "execute" }, "authenticated", new string[] { "create" }, false, true, DisplayName = "Stored-procedure with valid execute and invalid create permission")]
        public void InvalidCRUDForStoredProcedure(
            string role1,
            string[] operationsRole1,
            string role2,
            string[] operationsRole2,
            bool isValid,
            bool differentOperationDifferentRoleFailure)
        {
            List<EntityPermission> permissionSettings = new()
            {
                new(
                Role: role1,
                Actions: operationsRole1.Select(a => new EntityAction(EnumExtensions.Deserialize<EntityActionOperation>(a), null, new())).ToArray())
            };

            // Adding another role for the entity.
            if (role2 is not null && operationsRole2 is not null)
            {
                permissionSettings.Add(new(
                    Role: role2,
                    Actions: operationsRole2.Select(a => new EntityAction(EnumExtensions.Deserialize<EntityActionOperation>(a), null, new())).ToArray()));
            }

            EntitySource entitySource = new(
                    Type: EntitySourceType.StoredProcedure,
                    Object: "sourceName",
                    Parameters: null,
                    KeyFields: null
                );

            Entity testEntity = new(
                Source: entitySource,
                Rest: new(EntityRestOptions.DEFAULT_HTTP_VERBS_ENABLED_FOR_SP),
                GraphQL: new(AuthorizationHelpers.TEST_ENTITY, AuthorizationHelpers.TEST_ENTITY + "s"),
                Permissions: permissionSettings.ToArray(),
                Relationships: null,
                Mappings: null
            );

            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE
            ) with
            { Entities = new(new Dictionary<string, Entity>() { { AuthorizationHelpers.TEST_ENTITY, testEntity } }) };

            RuntimeConfigValidator configValidator = InitializeRuntimeConfigValidator();

            try
            {
                configValidator.ValidatePermissionsInConfig(runtimeConfig);
                Assert.AreEqual(true, isValid);
            }
            catch (DataApiBuilderException ex)
            {
                Assert.AreEqual(false, isValid);
                Assert.AreEqual(expected: $"Invalid operation for Entity: {AuthorizationHelpers.TEST_ENTITY}. " +
                            $"Stored procedures can only be configured with the 'execute' operation.", actual: ex.Message);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
                Assert.AreEqual(DataApiBuilderException.SubStatusCodes.ConfigValidationError, ex.SubStatusCode);
            }
        }

        /// <summary>
        /// Test method to validate that an appropriate exception is thrown when there is an invalid action
        /// supplied in the RuntimeConfig.
        /// </summary>
        /// <param name="dbPolicy">Database policy.</param>
        /// <param name="action">The action to be validated.</param>
        [DataTestMethod]
        [DataRow("@claims.id eq @item.col1", EntityActionOperation.Insert, DisplayName = "Invalid action Insert specified in config")]
        [DataRow("@claims.id eq @item.col2", EntityActionOperation.Upsert, DisplayName = "Invalid action Upsert specified in config")]
        [DataRow("@claims.id eq @item.col3", EntityActionOperation.UpsertIncremental, DisplayName = "Invalid action UpsertIncremental specified in config")]
        public void InvalidActionSpecifiedForARole(string dbPolicy, EntityActionOperation action)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: action,
                includedCols: new HashSet<string> { "col1", "col2", "col3" },
                databasePolicy: dbPolicy
                );

            RuntimeConfigValidator configValidator = InitializeRuntimeConfigValidator();

            // Assert that expected exception is thrown.
            DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() => configValidator.ValidatePermissionsInConfig(runtimeConfig));
            Assert.AreEqual($"action:{action} specified for entity:{AuthorizationHelpers.TEST_ENTITY}," +
                    $" role:{AuthorizationHelpers.TEST_ROLE} is not valid.", ex.Message);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
            Assert.AreEqual(DataApiBuilderException.SubStatusCodes.ConfigValidationError, ex.SubStatusCode);
        }

        /// <summary>
        /// Test that permission configuration validation fails when a database policy
        /// is defined for the Create operation for mysql/postgresql and passes for mssql.
        /// </summary>
        /// <param name="dbPolicy">Database policy.</param>
        /// <param name="errorExpected">Whether an error is expected.</param>
        [DataTestMethod]
        [DataRow(DatabaseType.PostgreSQL, "1 eq @item.col1", true, DisplayName = "Database Policy defined for Create fails for PostgreSQL")]
        [DataRow(DatabaseType.PostgreSQL, null, false, DisplayName = "Database Policy set as null for Create passes on PostgreSQL.")]
        [DataRow(DatabaseType.PostgreSQL, "", false, DisplayName = "Database Policy left empty for Create passes for PostgreSQL.")]
        [DataRow(DatabaseType.PostgreSQL, " ", false, DisplayName = "Database Policy only whitespace for Create passes for PostgreSQL.")]
        [DataRow(DatabaseType.MySQL, "1 eq @item.col1", true, DisplayName = "Database Policy defined for Create fails for MySQL")]
        [DataRow(DatabaseType.MySQL, null, false, DisplayName = "Database Policy set as for Create passes for MySQL")]
        [DataRow(DatabaseType.MySQL, "", false, DisplayName = "Database Policy left empty for Create passes for MySQL")]
        [DataRow(DatabaseType.MySQL, " ", false, DisplayName = "Database Policy only whitespace for Create passes for MySQL")]
        [DataRow(DatabaseType.MSSQL, "2 eq @item.col3", false, DisplayName = "Database Policy defined for Create passes for MSSQL")]
        public void AddDatabasePolicyToCreateOperation(DatabaseType dbType, string dbPolicy, bool errorExpected)
        {
            EntityActionOperation action = EntityActionOperation.Create;
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: action,
                includedCols: new HashSet<string> { "col1", "col2", "col3" },
                databasePolicy: dbPolicy,
                dbType: dbType
            );

            try
            {
                RuntimeConfigValidator configValidator = InitializeRuntimeConfigValidator();
                configValidator.ValidatePermissionsInConfig(runtimeConfig);
                Assert.IsFalse(errorExpected, message: "Validation expected to have failed.");
            }
            catch (DataApiBuilderException ex)
            {
                Assert.IsTrue(errorExpected, message: "Validation expected to have passed.");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
                Assert.AreEqual(DataApiBuilderException.SubStatusCodes.ConfigValidationError, ex.SubStatusCode);
            }
        }

        /// <summary>
        /// Test method to check that Exception is thrown when Target Entity used in relationship is not defined in the config.
        /// </summary>
        [TestMethod]
        public void TestAddingRelationshipWithInvalidTargetEntity()
        {
            Dictionary<string, EntityRelationship> relationshipMap = new();

            // Creating relationship with an Invalid entity in relationship
            EntityRelationship sampleRelationship = new(
                Cardinality: Cardinality.One,
                TargetEntity: "INVALID_ENTITY",
                SourceFields: null,
                TargetFields: null,
                LinkingObject: null,
                LinkingSourceFields: null,
                LinkingTargetFields: null
            );

            relationshipMap.Add("rname1", sampleRelationship);

            Entity sampleEntity1 = GetSampleEntityUsingSourceAndRelationshipMap(
                source: "TEST_SOURCE1",
                relationshipMap: relationshipMap,
                graphQLDetails: new("SampleEntity1", "rname1s", true)
            );

            Dictionary<string, Entity> entityMap = new()
            {
                { "SampleEntity1", sampleEntity1 }
            };

            RuntimeConfig runtimeConfig = new(
                Schema: "UnitTestSchema",
                DataSource: new DataSource(DatabaseType: DatabaseType.MSSQL, "", Options: null),
                Runtime: new(
                    Rest: new(),
                    GraphQL: new(),
                    Mcp: null,
                    Host: new(null, null)
                ),
                Entities: new(entityMap)
            );

            RuntimeConfigValidator configValidator = InitializeRuntimeConfigValidator();

            // Assert that expected exception is thrown. Entity used in relationship is Invalid
            DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() =>
                configValidator.ValidateRelationshipConfigCorrectness(runtimeConfig));
            Assert.AreEqual($"Entity: {sampleRelationship.TargetEntity} used for relationship is not defined in the config.", ex.Message);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        }

        /// <summary>
        /// Test method to check that Exception is thrown when Entity used in the relationship has GraphQL disabled.
        /// </summary>
        [TestMethod]
        public void TestAddingRelationshipWithDisabledGraphQL()
        {
            // creating entity with disabled graphQL
            Entity sampleEntity1 = GetSampleEntityUsingSourceAndRelationshipMap(
                source: "TEST_SOURCE1",
                relationshipMap: null,
                graphQLDetails: new("", "", false)
            );

            Dictionary<string, EntityRelationship> relationshipMap = new();

            EntityRelationship sampleRelationship = new(
                Cardinality: Cardinality.One,
                TargetEntity: "SampleEntity1",
                SourceFields: null,
                TargetFields: null,
                LinkingObject: null,
                LinkingSourceFields: null,
                LinkingTargetFields: null
            );

            relationshipMap.Add("rname1", sampleRelationship);

            // Adding relationshipMap to SampleEntity1 (which has GraphQL enabled)
            Entity sampleEntity2 = GetSampleEntityUsingSourceAndRelationshipMap(
                source: "TEST_SOURCE2",
                relationshipMap: relationshipMap,
                graphQLDetails: new("", "", true)
            );

            Dictionary<string, Entity> entityMap = new()
            {
                { "SampleEntity1", sampleEntity1 },
                { "SampleEntity2", sampleEntity2 }
            };

            RuntimeConfig runtimeConfig = new(
                Schema: "UnitTestSchema",
                DataSource: new DataSource(DatabaseType: DatabaseType.MSSQL, "", Options: null),
                Runtime: new(
                    Rest: new(),
                    GraphQL: new(),
                    Mcp: null,
                    Host: new(null, null)
                ),
                Entities: new(entityMap)
            );

            RuntimeConfigValidator configValidator = InitializeRuntimeConfigValidator();

            // Exception should be thrown as we cannot use an entity (with graphQL disabled) in a relationship.
            DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() =>
                configValidator.ValidateRelationshipConfigCorrectness(runtimeConfig));
            Assert.AreEqual($"Entity: {sampleRelationship.TargetEntity} is disabled for GraphQL.", ex.Message);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        }

        /// <summary>
        /// Test method to check that an exception is thrown in a many-many relationship (LinkingObject was provided)
        /// while linkingSourceFields and sourceFields are null, or targetFields and linkingTargetFields are null,
        /// and also the relationship is not defined in the database through foreign keys on the missing side of
        /// fields in the config for the many-many relationship. That means if source and linking source fields are
        /// missing that the foreign key information does not exist in the database for source entity to linking object,
        /// and if target and linking target fields are missing that the foreign key information does not exist in the
        /// database for the target entity to linking object.
        /// Further verify that after adding said foreignKeyPair in the Database, no exception is thrown. This is because
        /// once we have that foreign key information we can complete that side of the many-many relationship
        /// from that foreign key.
        /// </summary>
        [DataRow(null, null, new string[] { "targetField" }, new string[] { "linkingTargetField" }, "SampleEntity1",
            DisplayName = "sourceFields and LinkingSourceFields are null")]
        [DataRow(new string[] { "sourceField" }, new string[] { "linkingSourceField" }, null, null, "SampleEntity2",
            DisplayName = "targetFields and LinkingTargetFields are null")]
        [DataTestMethod]
        public void TestRelationshipWithLinkingObjectNotHavingRequiredFields(
            string[] sourceFields,
            string[] linkingSourceFields,
            string[] targetFields,
            string[] linkingTargetFields,
            string relationshipEntity
        )
        {
            // Creating an EntityMap with two sample entity
            Dictionary<string, Entity> entityMap = GetSampleEntityMap(
                sourceEntity: "SampleEntity1",
                targetEntity: "SampleEntity2",
                sourceFields: sourceFields,
                targetFields: targetFields,
                linkingObject: "TEST_SOURCE_LINK",
                linkingSourceFields: linkingSourceFields,
                linkingTargetFields: linkingTargetFields
            );

            RuntimeConfig runtimeConfig = new(
                Schema: "UnitTestSchema",
                DataSource: new DataSource(DatabaseType: DatabaseType.MSSQL, "", Options: null),
                Runtime: new(
                    Rest: new(),
                    GraphQL: new(),
                    Mcp: null,
                    Host: new(null, null),
                    BaseRoute: null,
                    Telemetry: null,
                    Cache: null,
                    Pagination: null,
                    Health: null
                ),
                Entities: new(entityMap)
            );

            // Mocking EntityToDatabaseObject
            MockFileSystem fileSystem = new();
            FileSystemRuntimeConfigLoader loader = new(fileSystem);
            RuntimeConfigProvider provider = new(loader) { IsLateConfigured = true };
            RuntimeConfigValidator configValidator = new(provider, fileSystem, new Mock<ILogger<RuntimeConfigValidator>>().Object);
            Mock<ISqlMetadataProvider> _sqlMetadataProvider = new();

            Dictionary<string, DatabaseObject> mockDictionaryForEntityDatabaseObject = new()
            {
                {
                    "SampleEntity1",
                    new DatabaseTable("dbo", "TEST_SOURCE1")
                },

                {
                    "SampleEntity2",
                    new DatabaseTable("dbo", "TEST_SOURCE2")
                }
            };

            _sqlMetadataProvider.Setup(x => x.EntityToDatabaseObject).Returns(mockDictionaryForEntityDatabaseObject);

            // To mock the schema name and dbObjectName for linkingObject
            _sqlMetadataProvider.Setup(x =>
                x.ParseSchemaAndDbTableName("TEST_SOURCE_LINK")).Returns(("dbo", "TEST_SOURCE_LINK"));

            string discard;
            _sqlMetadataProvider.Setup(x => x.TryGetExposedColumnName(It.IsAny<string>(), It.IsAny<string>(), out discard)).Returns(true);

            Mock<IMetadataProviderFactory> _metadataProviderFactory = new();
            _metadataProviderFactory.Setup(x => x.GetMetadataProvider(It.IsAny<string>())).Returns(_sqlMetadataProvider.Object);

            // Exception thrown as foreignKeyPair not found in the DB.
            DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() =>
                configValidator.ValidateRelationships(runtimeConfig, _metadataProviderFactory.Object));
            Assert.AreEqual($"Could not find relationship between Linking Object: TEST_SOURCE_LINK"
                + $" and entity: {relationshipEntity}.", ex.Message);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);

            // Mocking ForeignKeyPair to be defined In DB
            _sqlMetadataProvider.Setup(x =>
                x.VerifyForeignKeyExistsInDB(
                    new DatabaseTable("dbo", "TEST_SOURCE_LINK"), new DatabaseTable("dbo", "TEST_SOURCE1")
                )).Returns(true);

            _sqlMetadataProvider.Setup(x =>
                x.VerifyForeignKeyExistsInDB(
                    new DatabaseTable("dbo", "TEST_SOURCE_LINK"), new DatabaseTable("dbo", "TEST_SOURCE2")
                )).Returns(true);

            // Since, we have defined the relationship in Database,
            // the engine was able to find foreign key relation and validation will pass.
            configValidator.ValidateRelationships(runtimeConfig, _metadataProviderFactory.Object);
        }

        /// <summary>
        /// Test method to check that an exception is thrown when the relationship is one-many
        /// or many-one (determined by the linking object being null), while both SourceFields
        /// and TargetFields are null in the config, and the foreignKey pair between source and target
        /// is not defined in the database as well.
        /// Also verify that after adding foreignKeyPair between the source and target entities in the Database,
        /// no exception is thrown.
        /// </summary>
        [TestMethod]
        public void TestRelationshipWithNoLinkingObjectAndSourceAndTargetFieldsNull()
        {
            // Creating an EntityMap with two sample entity
            Dictionary<string, Entity> entityMap = GetSampleEntityMap(
                sourceEntity: "SampleEntity1",
                targetEntity: "SampleEntity2",
                sourceFields: null,
                targetFields: null,
                linkingObject: null,
                linkingSourceFields: null,
                linkingTargetFields: null
            );

            RuntimeConfig runtimeConfig = new(
                Schema: "UnitTestSchema",
                DataSource: new DataSource(DatabaseType: DatabaseType.MSSQL, "", Options: null),
                Runtime: new(
                    Rest: new(),
                    GraphQL: new(),
                    Mcp: null,
                    Host: new(null, null),
                    BaseRoute: null,
                    Telemetry: null,
                    Cache: null,
                    Pagination: null,
                    Health: null
                ),
                Entities: new(entityMap)
            );

            MockFileSystem fileSystem = new();
            FileSystemRuntimeConfigLoader loader = new(fileSystem);
            RuntimeConfigProvider provider = new(loader) { IsLateConfigured = true };
            RuntimeConfigValidator configValidator = new(provider, fileSystem, new Mock<ILogger<RuntimeConfigValidator>>().Object);
            Mock<ISqlMetadataProvider> _sqlMetadataProvider = new();

            Dictionary<string, DatabaseObject> mockDictionaryForEntityDatabaseObject = new()
            {
                {
                    "SampleEntity1",
                    new DatabaseTable("dbo", "TEST_SOURCE1")
                },

                {
                    "SampleEntity2",
                    new DatabaseTable("dbo", "TEST_SOURCE2")
                }
            };

            _sqlMetadataProvider.Setup<Dictionary<string, DatabaseObject>>(x =>
                x.EntityToDatabaseObject).Returns(mockDictionaryForEntityDatabaseObject);

            Mock<IMetadataProviderFactory> _metadataProviderFactory = new();
            _metadataProviderFactory.Setup(x => x.GetMetadataProvider(It.IsAny<string>())).Returns(_sqlMetadataProvider.Object);

            // Exception is thrown as foreignKey pair is not specified in the config, nor defined
            // in the database.
            DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() =>
                configValidator.ValidateRelationships(runtimeConfig, _metadataProviderFactory.Object));
            Assert.AreEqual($"Could not find relationship between entities:"
                + $" SampleEntity1 and SampleEntity2.", ex.Message);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);

            // Mocking ForeignKeyPair to be defined In DB
            _sqlMetadataProvider.Setup<bool>(x =>
                x.VerifyForeignKeyExistsInDB(
                    new DatabaseTable("dbo", "TEST_SOURCE1"), new DatabaseTable("dbo", "TEST_SOURCE2")
                )).Returns(true);

            _sqlMetadataProvider.Setup<bool>(x =>
                x.VerifyForeignKeyExistsInDB(
                    new DatabaseTable("dbo", "TEST_SOURCE2"), new DatabaseTable("dbo", "TEST_SOURCE1")
                )).Returns(true);

            _metadataProviderFactory.Setup(x => x.GetMetadataProvider(It.IsAny<string>())).Returns(_sqlMetadataProvider.Object);

            // No Exception is thrown as foreignKey Pair was found in the DB between
            // source and target entity.
            configValidator.ValidateRelationships(runtimeConfig, _metadataProviderFactory.Object);
        }

        /// <summary>
        /// Test method that ensures our validation code catches the cases where source and target fields do not match in some way
        /// and the linking object is null, indicating we have a one-many or many-one relationship.
        /// Not matching can either be because one is null and the other is not, or because they have a different number of fields.
        /// </summary>
        /// <param name="sourceFields">List of strings representing the source fields.</param>
        /// <param name="targetFields">List of strings representing the target fields.</param>
        /// <param name="expectedExceptionMessage">The error message we expect from validation.</param>
        [DataRow(new[] { "sourceFields" }, null, "Entity: SampleEntity1 has a relationship: rname1, which has source and target fields where one is null and the other is not.",
            DisplayName = "Linking object is null and sourceFields exist but targetFields are null.")]
        [DataRow(null, new[] { "targetFields" }, "Entity: SampleEntity1 has a relationship: rname1, which has source and target fields where one is null and the other is not.",
            DisplayName = "Linking object is null and targetFields exist but sourceFields are null")]
        [DataRow(new[] { "A", "B", "C" }, new[] { "1", "2" }, "Entity: SampleEntity1 has a relationship: rname1, which has 3 source fields defined, but 2 target fields defined.",
            DisplayName = "Linking object is null and sourceFields and targetFields have different length.")]
        [DataTestMethod]
        public void TestRelationshipWithoutSourceAndTargetFieldsMatching(
            string[] sourceFields,
            string[] targetFields,
            string expectedExceptionMessage)
        {
            // Creating an EntityMap with two sample entity
            Dictionary<string, Entity> entityMap = GetSampleEntityMap(
                sourceEntity: "SampleEntity1",
                targetEntity: "SampleEntity2",
                sourceFields: sourceFields,
                targetFields: targetFields,
                linkingObject: null,
                linkingSourceFields: null,
                linkingTargetFields: null
            );

            RuntimeConfig runtimeConfig = new(
                Schema: "UnitTestSchema",
                DataSource: new DataSource(DatabaseType: DatabaseType.MSSQL, "", Options: null),
                Runtime: new(
                    Rest: new(),
                    GraphQL: new(),
                    Mcp: null,
                    Host: new(null, null),
                    BaseRoute: null,
                    Telemetry: null,
                    Cache: null,
                    Pagination: null,
                    Health: null
                ),
                Entities: new(entityMap));

            MockFileSystem fileSystem = new();
            FileSystemRuntimeConfigLoader loader = new(fileSystem);
            RuntimeConfigProvider provider = new(loader) { IsLateConfigured = true };
            RuntimeConfigValidator configValidator = new(provider, fileSystem, new Mock<ILogger<RuntimeConfigValidator>>().Object);

            Dictionary<string, DatabaseObject> mockDictionaryForEntityDatabaseObject = new()
            {
                {
                    "SampleEntity1",
                    new DatabaseTable("dbo", "TEST_SOURCE1")
                },

                {
                    "SampleEntity2",
                    new DatabaseTable("dbo", "TEST_SOURCE2")
                }
            };

            // Exception is thrown since sourceFields and targetFields do not match in either their existence,
            // or their length.
            DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() =>
                configValidator.ValidateRelationshipConfigCorrectness(runtimeConfig));
            Assert.AreEqual(expectedExceptionMessage, ex.Message);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
            Assert.AreEqual(DataApiBuilderException.SubStatusCodes.ConfigValidationError, ex.SubStatusCode);
        }

        /// <summary>
        /// This test checks that the final config used by runtime engine doesn't lose the directory information
        /// if provided by the user.
        /// It also validates that if config file is provided by the user, it will be used directly irrespective of
        /// environment variable being set or not. 
        /// When user doesn't provide a config file, we check if environment variable is set and if it is, we use
        /// the config file specified by the environment variable, else we use the default config file.
        /// <param name="userProvidedConfigFilePath"></param>
        /// <param name="environmentValue"></param>
        /// <param name="useAbsolutePath"></param>
        /// <param name="environmentFile"></param>
        /// <param name="finalConfigFilePath"></param>
        [DataTestMethod]
        [DataRow("my-config.json", "", false, null, "my-config.json", DisplayName = "Config file in the current directory provided by user and environment variable is not set")]
        [DataRow("test-configs/my-config.json", "", false, null, "test-configs/my-config.json", DisplayName = "Config file in different directory provided by user and environment variable is not set")]
        [DataRow("my-config.json", "Test", false, "my-config.Test.json", "my-config.json", DisplayName = "Config file in the current directory provided by user and environment variable is set")]
        [DataRow("test-configs/my-config.json", "Test", false, "test-configs/my-config.Test.json", "test-configs/my-config.json", DisplayName = "Config file in different directory provided by user and environment variable is set")]
        [DataRow("my-config.json", "Test", false, "dab-config.Test.json", "my-config.json", DisplayName = "Config file in the current directory provided by user and environment variable is set and environment file is present")]
        [DataRow("test-configs/my-config.json", "Test", false, "test-configs/dab-config.Test.json", "test-configs/my-config.json", DisplayName = "Config file in different directory provided by user and environment variable is set and environment file is present")]
        [DataRow("my-config.json", "", true, null, "my-config.json", DisplayName = "Config file in the current directory provided by user and environment variable is not set and absolute path is provided")]
        [DataRow("test-configs/my-config.json", "", true, null, "test-configs/my-config.json", DisplayName = "Config file in different directory provided by user and environment variable is not set and absolute path is provided")]
        [DataRow("my-config.json", "Test", true, "my-config.Test.json", "my-config.json", DisplayName = "Config file in the current directory provided by user and environment variable is set and absolute path is provided")]
        [DataRow("test-configs/my-config.json", "Test", true, "test-configs/my-config.Test.json", "test-configs/my-config.json", DisplayName = "Config file in different directory provided by user and environment variable is set and absolute path is provided")]
        [DataRow("my-config.json", "Test", true, "dab-config.Test.json", "my-config.json", DisplayName = "Config file in the current directory provided by user and environment variable is set and environment file is present and absolute path is provided")]
        [DataRow("test-configs/my-config.json", "Test", true, "test-configs/dab-config.Test.json", "test-configs/my-config.json", DisplayName = "Config file in the different directory provided by user and environment variable is set and environment file is present and absolute path is provided")]
        [DataRow(null, "", false, null, "dab-config.json", DisplayName = "Config file not provided by user and environment variable is not set")]
        [DataRow(null, "Test", false, "dab-config.Test.json", "dab-config.json", DisplayName = "Config file not provided by user and environment variable is set and environment file is present")]
        [DataRow(null, "Test", false, null, "dab-config.json", DisplayName = "Config file not provided by user and environment variable is set and environment file is not present")]
        public void TestCorrectConfigFileIsSelectedForRuntimeEngine(
            string userProvidedConfigFilePath,
            string environmentValue,
            bool useAbsolutePath,
            string environmentFile,
            string finalConfigFilePath)
        {
            MockFileSystem fileSystem = new();
            if (!string.IsNullOrWhiteSpace(Path.GetDirectoryName(userProvidedConfigFilePath)))
            {
                fileSystem.AddDirectory("test-configs");
            }

            if (useAbsolutePath)
            {
                userProvidedConfigFilePath = fileSystem.Path.GetFullPath(userProvidedConfigFilePath);
                finalConfigFilePath = fileSystem.Path.GetFullPath(finalConfigFilePath);
            }

            if (environmentFile is not null)
            {
                fileSystem.AddEmptyFile(environmentFile);
            }

            FileSystemRuntimeConfigLoader runtimeConfigLoader;
            if (userProvidedConfigFilePath is not null)
            {
                runtimeConfigLoader = new(fileSystem, handler: null, userProvidedConfigFilePath);
            }
            else
            {
                runtimeConfigLoader = new(fileSystem);
            }

            Assert.AreEqual(finalConfigFilePath, runtimeConfigLoader.ConfigFilePath);
        }

        /// <summary>
        /// Method to validate that runtimeConfig is successfully set up using constructor
        /// where members are passed in without json config file.
        /// RuntimeConfig has two constructors, one that loads from the config json and one that takes in all the members.
        /// This test makes sure that the constructor that takes in all the members works as expected.
        /// </summary>
        [TestMethod]
        public void TestRuntimeConfigSetupWithNonJsonConstructor()
        {
            EntitySource entitySource = new(
                "sourceName",
                EntitySourceType.Table,
                null,
                null
            );

            Entity sampleEntity1 = new(
                Source: entitySource,
                GraphQL: null,
                Rest: null,
                Permissions: null,
                Mappings: null,
                Relationships: null);

            string entityName = "SampleEntity1";
            string dataSourceName = "Test1";

            Dictionary<string, Entity> entityMap = new()
            {
                { entityName, sampleEntity1 }
            };

            DataSource testDataSource = new(DatabaseType: DatabaseType.MSSQL, "", Options: null);
            Dictionary<string, DataSource> dataSourceNameToDataSource = new()
            {
                { dataSourceName, testDataSource }
            };

            Dictionary<string, string> entityNameToDataSourceName = new()
            {
                { entityName, dataSourceName }
            };

            RuntimeConfig runtimeConfig = new(
                Schema: "UnitTestSchema",
                DataSource: testDataSource,
                Runtime: new(
                    Rest: new(),
                    GraphQL: new(),
                    Mcp: new(),
                    Host: new(null, null)
                ),
                Entities: new RuntimeEntities(entityMap),
                DefaultDataSourceName: dataSourceName,
                DataSourceNameToDataSource: dataSourceNameToDataSource,
                EntityNameToDataSourceName: entityNameToDataSourceName
            );

            Assert.AreEqual(testDataSource, runtimeConfig.DataSource, "RuntimeConfig datasource must match datasource passed into constructor");
            Assert.AreEqual(dataSourceNameToDataSource.Count(), runtimeConfig.ListAllDataSources().Count(),
                "RuntimeConfig datasource count must match datasource count passed into constructor");
            Assert.IsTrue(runtimeConfig.SqlDataSourceUsed,
                $"Config has a sql datasource and member {nameof(runtimeConfig.SqlDataSourceUsed)} must be marked as true.");
            Assert.IsFalse(runtimeConfig.CosmosDataSourceUsed,
                $"Config does not have a cosmos datasource and member {nameof(runtimeConfig.CosmosDataSourceUsed)} must be marked as false.");
        }

        /// <summary>
        /// Test to validate pagination options.
        /// NOTE: Changing the default values of default page size and max page size would be a breaking change.
        /// </summary>
        /// <param name="exceptionExpected">Should there be an exception.</param>
        /// <param name="defaultPageSize">default page size to go into config.</param>
        /// <param name="maxPageSize">max page size to go into config.</param>
        /// <param name="expectedExceptionMessage">expected exception message in case there is exception.</param>
        /// <param name="expectedDefaultPageSize">expected default page size from config.</param>
        /// <param name="expectedMaxPageSize">expected max page size from config.</param>
        [DataTestMethod]
        [DataRow(false, null, null, "", (int)PaginationOptions.DEFAULT_PAGE_SIZE, (int)PaginationOptions.MAX_PAGE_SIZE,
            DisplayName = "MaxPageSize should be 100,000 and DefaultPageSize should be 100 when no value provided in config.")]
        [DataRow(false, 1000, 10000, "", 1000, 10000,
            DisplayName = "Valid inputs of MaxPageSize and DefaultPageSize must be accepted and set in the config.")]
        [DataRow(false, -1, 10000, "", 10000, 10000,
            DisplayName = "DefaultPageSize should be the same as MaxPageSize when DefaultPageSize is -1 in config.")]
        [DataRow(false, 100, -1, "", 100, Int32.MaxValue,
            DisplayName = "MaxPageSize should be assigned UInt32.MaxValue when MaxPageSize is -1 in config.")]
        [DataRow(true, 100, 0, "Pagination options invalid. Page size arguments cannot be 0, exceed max int value or be less than -1",
            DisplayName = "MaxPageSize cannot be 0")]
        [DataRow(true, 0, 100, "Pagination options invalid. Page size arguments cannot be 0, exceed max int value or be less than -1",
            DisplayName = "DefaultPageSize cannot be 0")]
        [DataRow(true, 101, 100, "Pagination options invalid. The default page size cannot be greater than max page size",
            DisplayName = "DefaultPageSize cannot be greater than MaxPageSize")]
        public void ValidatePaginationOptionsInConfig(
            bool exceptionExpected,
            int? defaultPageSize,
            int? maxPageSize,
            string expectedExceptionMessage,
            int? expectedDefaultPageSize = null,
            int? expectedMaxPageSize = null)
        {
            try
            {
                RuntimeConfig runtimeConfig = new(
                    Schema: "UnitTestSchema",
                    DataSource: new DataSource(DatabaseType: DatabaseType.MSSQL, "", Options: null),
                    Runtime: new(
                        Rest: new(),
                        GraphQL: new(),
                        Mcp: new(),
                        Host: new(Cors: null, Authentication: null),
                        Pagination: new PaginationOptions(defaultPageSize, maxPageSize)
                    ),
                    Entities: new(new Dictionary<string, Entity>()));

                Assert.AreEqual((uint)expectedDefaultPageSize, runtimeConfig.DefaultPageSize());
                Assert.AreEqual((uint)expectedMaxPageSize, runtimeConfig.MaxPageSize());
            }
            catch (DataApiBuilderException dabException)
            {
                Assert.IsTrue(exceptionExpected);
                Assert.AreEqual(expectedExceptionMessage, dabException.Message);
                Assert.AreEqual(expected: HttpStatusCode.ServiceUnavailable, actual: dabException.StatusCode);
                Assert.AreEqual(expected: DataApiBuilderException.SubStatusCodes.ConfigValidationError, actual: dabException.SubStatusCode);
            }
        }

        /// <summary>
        /// Test to validate the max response size option in the runtime config.
        /// Note:Changing the default values of max response size would be a breaking change.
        /// </summary>
        /// <param name="exceptionExpected">should there be an exception.</param>
        /// <param name="maxDbResponseSizeMB">maxResponse size input</param>
        /// <param name="expectedExceptionMessage">expected exception message in case there is exception.</param>
        /// <param name="expectedMaxResponseSize">expected value in config.</param>
        [DataTestMethod]
        [DataRow(false, 158, false, "",
            DisplayName = $"{nameof(RuntimeConfig.Runtime.Host.MaxResponseSizeMB)} should be 158MB when no value provided in config.")]
        [DataRow(false, 64, false, "",
            DisplayName = $"Valid positive input of {nameof(RuntimeConfig.Runtime.Host.MaxResponseSizeMB)}  > 0 and <= 158MB must be accepted and set in the config.")]
        [DataRow(false, -1, false, "",
            DisplayName = $"-1 user input for {nameof(RuntimeConfig.Runtime.Host.MaxResponseSizeMB)} should result in a value of 158MB which is the max value supported by dab engine")]
        [DataRow(false, 0, null, true, "Max response size cannot be 0, exceed 158MB or be less than -1",
            DisplayName = $"Input of 0 for {nameof(RuntimeConfig.Runtime.Host.MaxResponseSizeMB)} must throw exception.")]
        [DataRow(false, 159, null, true, "Max response size cannot be 0, exceed 158MB or be less than -1",
            DisplayName = $"Inputs of {nameof(RuntimeConfig.Runtime.Host.MaxResponseSizeMB)} greater than 158MB must throw exception.")]
        public void ValidateMaxResponseSizeInConfig(
            int? maxDbResponseSizeMB,
            int? expectedMaxResponseSizeMB,
            bool exceptionExpected,
            string expectedExceptionMessage)
        {
            try
            {
                RuntimeConfig runtimeConfig = new(
                    Schema: "UnitTestSchema",
                    DataSource: new DataSource(DatabaseType: DatabaseType.MSSQL, "", Options: null),
                    Runtime: new(
                        Rest: new(),
                        GraphQL: new(),
                        Mcp: new(),
                        Host: new(Cors: null, Authentication: null, MaxResponseSizeMB: maxDbResponseSizeMB)
                    ),
                    Entities: new(new Dictionary<string, Entity>()));
                Assert.AreEqual(expectedMaxResponseSizeMB, runtimeConfig.MaxResponseSizeMB());
            }
            catch (DataApiBuilderException ex)
            {
                Assert.IsTrue(exceptionExpected);
                Assert.AreEqual(expectedExceptionMessage, ex.Message);
            }
        }

        private static RuntimeConfigValidator InitializeRuntimeConfigValidator()
        {
            MockFileSystem fileSystem = new();
            FileSystemRuntimeConfigLoader loader = new(fileSystem);
            RuntimeConfigProvider provider = new(loader);
            return new(provider, fileSystem, new Mock<ILogger<RuntimeConfigValidator>>().Object);
        }

        // Helper method to create a sample entity with source and relationship map
        private static Entity GetSampleEntityUsingSourceAndRelationshipMap(string source, Dictionary<string, EntityRelationship> relationshipMap, EntityGraphQLOptions graphQLDetails)
        {
            return new Entity(
                Source: new EntitySource(source, EntitySourceType.Table, null, null),
                GraphQL: graphQLDetails,
                Rest: null,
                Permissions: null,
                Mappings: null,
                Relationships: relationshipMap
            );
        }

        // Helper method to create a sample entity map for relationship tests
        private static Dictionary<string, Entity> GetSampleEntityMap(
            string sourceEntity,
            string targetEntity,
            string[] sourceFields,
            string[] targetFields,
            string linkingObject,
            string[] linkingSourceFields,
            string[] linkingTargetFields)
        {
            Dictionary<string, EntityRelationship> relationshipMap = new();
            if (targetEntity != null)
            {
                relationshipMap["rname1"] = new EntityRelationship(
                    Cardinality: Cardinality.One,
                    TargetEntity: targetEntity,
                    SourceFields: sourceFields,
                    TargetFields: targetFields,
                    LinkingObject: linkingObject,
                    LinkingSourceFields: linkingSourceFields,
                    LinkingTargetFields: linkingTargetFields
                );
            }

            Dictionary<string, Entity> entityMap = new()
            {
                { sourceEntity, new Entity(
                    Source: new EntitySource(sourceEntity, EntitySourceType.Table, null, null),
                    GraphQL: new EntityGraphQLOptions(sourceEntity, sourceEntity + "s", true),
                    Rest: null,
                    Permissions: null,
                    Mappings: null,
                    Relationships: relationshipMap.Count > 0 ? relationshipMap : null
                ) },
                { targetEntity, new Entity(
                    Source: new EntitySource(targetEntity, EntitySourceType.Table, null, null),
                    GraphQL: new EntityGraphQLOptions(targetEntity, targetEntity + "s", true),
                    Rest: null,
                    Permissions: null,
                    Mappings: null,
                    Relationships: null
                ) }
            };
            return entityMap;
        }
    }
}
