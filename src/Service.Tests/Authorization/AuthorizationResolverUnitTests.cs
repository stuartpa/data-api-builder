// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.DataApiBuilder.Auth;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Core.Authorization;
using Azure.DataApiBuilder.Service.Exceptions;
using Azure.DataApiBuilder.Service.Tests.Authentication.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Azure.DataApiBuilder.Service.Tests.Authorization
{
    [TestClass]
    public class AuthorizationResolverUnitTests
    {
        private const string TEST_ENTITY = "SampleEntity";
        private const string TEST_ROLE = "Writer";
        private const EntityActionOperation TEST_OPERATION = EntityActionOperation.Create;
        private const string TEST_AUTHENTICATION_TYPE = "TestAuth";
        private const string TEST_CLAIMTYPE_NAME = "TestName";

        #region Role Context Tests
        /// <summary>
        /// When the client role header is present, validates result when
        /// Role is in ClaimsPrincipal.Roles -> VALID
        /// Role is NOT in ClaimsPrincipal.Roles -> INVALID
        /// </summary>
        [DataTestMethod]
        [DataRow("Reader", true, true)]
        [DataRow("Reader", false, false)]
        public void ValidRoleContext_Simple(string clientRoleHeaderValue, bool userIsInRole, bool expected)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig();
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Mock<HttpContext> context = new();
            context.SetupGet(x => x.Request.Headers[AuthorizationResolver.CLIENT_ROLE_HEADER]).Returns(clientRoleHeaderValue);
            context.Setup(x => x.User.IsInRole(clientRoleHeaderValue)).Returns(userIsInRole);
            context.Setup(x => x.User.Identity!.IsAuthenticated).Returns(true);

            Assert.AreEqual(authZResolver.IsValidRoleContext(context.Object), expected);
        }

        [TestMethod("Role header has no value")]
        public void RoleHeaderEmpty()
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig();
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Mock<HttpContext> context = new();
            context.SetupGet(x => x.Request.Headers[AuthorizationResolver.CLIENT_ROLE_HEADER]).Returns(StringValues.Empty);
            bool expected = false;

            Assert.AreEqual(authZResolver.IsValidRoleContext(context.Object), expected);
        }

        [TestMethod("Role header has multiple values")]
        public void RoleHeaderDuplicated()
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig();
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Mock<HttpContext> context = new();
            StringValues multipleValuesForHeader = new(new string[] { "Reader", "Writer" });
            context.SetupGet(x => x.Request.Headers[AuthorizationResolver.CLIENT_ROLE_HEADER]).Returns(multipleValuesForHeader);
            context.Setup(x => x.User.IsInRole("Reader")).Returns(true);
            bool expected = false;
            Assert.AreEqual(authZResolver.IsValidRoleContext(context.Object), expected);
        }

        [TestMethod("Role header is missing")]
        public void NoRoleHeader_RoleContextTest()
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig();
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Mock<HttpContext> context = new();
            context.SetupGet(x => x.Request.Headers[AuthorizationResolver.CLIENT_ROLE_HEADER]).Returns(StringValues.Empty);
            bool expected = false;

            Assert.AreEqual(authZResolver.IsValidRoleContext(context.Object), expected);
        }
        #endregion

        #region Role and Operation on Entity Tests

        /// <summary>
        /// Tests the AreRoleAndOperationDefinedForEntity stage of authorization.
        /// Request operation is defined for role -> VALID
        /// Request operation not defined for role (role has 0 defined operations)
        ///     Ensures method short circuits in circumstances role is not defined -> INVALID
        /// Request operation does not match an operation defined for role (role has >=1 defined operation) -> INVALID
        /// </summary>
        [DataTestMethod]
        [DataRow("Writer", EntityActionOperation.Create, "Writer", EntityActionOperation.Create, true)]
        [DataRow("Reader", EntityActionOperation.Create, "Reader", EntityActionOperation.None, false)]
        [DataRow("Writer", EntityActionOperation.Create, "Writer", EntityActionOperation.Update, false)]
        public void AreRoleAndOperationDefinedForEntityTest(
            string configRole,
            EntityActionOperation configOperation,
            string roleName,
            EntityActionOperation operation,
            bool expected)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: configRole,
                operation: configOperation);
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            // Mock Request Values
            Assert.AreEqual(expected, authZResolver.AreRoleAndOperationDefinedForEntity(AuthorizationHelpers.TEST_ENTITY, roleName, operation));
        }

        /// <summary>
        /// Test that wildcard operation are expanded to explicit operations.
        /// Verifies that internal data structure are created correctly.
        /// </summary>
        [TestMethod("Wildcard operation is expanded to all valid operations")]
        public void TestWildcardOperation()
        {
            List<string> expectedRoles = new() { AuthorizationHelpers.TEST_ROLE };

            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.All);

            // Override the permission operations to be a list of operations for wildcard
            // instead of a list of objects created by readAction, updateAction
            Entity entity = runtimeConfig.Entities[AuthorizationHelpers.TEST_ENTITY];
            entity = entity with { Permissions = new[] { new EntityPermission(AuthorizationHelpers.TEST_ROLE, new EntityAction[] { new(EntityActionOperation.All, null, new(null, null)) }) } };
            runtimeConfig = runtimeConfig with { Entities = new(new Dictionary<string, Entity> { { AuthorizationHelpers.TEST_ENTITY, entity } }) };

            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            // There should not be a wildcard operation in AuthorizationResolver.EntityPermissionsMap
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.All));

            // The wildcard operation should be expanded to all the explicit operations.
            foreach (EntityActionOperation operation in EntityAction.ValidPermissionOperations)
            {
                Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(
                    AuthorizationHelpers.TEST_ENTITY,
                    AuthorizationHelpers.TEST_ROLE,
                    operation));

                IEnumerable<string> actualRolesForCol1 = authZResolver.GetRolesForField(AuthorizationHelpers.TEST_ENTITY, "col1", operation);

                CollectionAssert.AreEquivalent(expectedRoles, actualRolesForCol1.ToList());

                IEnumerable<string> actualRolesForOperation = IAuthorizationResolver.GetRolesForOperation(
                    AuthorizationHelpers.TEST_ENTITY,
                    operation,
                    authZResolver.EntityPermissionsMap);
                CollectionAssert.AreEquivalent(expectedRoles, actualRolesForOperation.ToList());
            }

            // Validate that the authorization check fails because the operations are invalid.
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(AuthorizationHelpers.TEST_ENTITY, TEST_ROLE, EntityActionOperation.Insert));
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(AuthorizationHelpers.TEST_ENTITY, TEST_ROLE, EntityActionOperation.Upsert));
        }

        /// <summary>
        /// Verify that the internal data structure is created correctly when we have
        /// Two roles for the same entity with different permission.
        /// readOnlyRole - Read permission only for col1 and no policy.
        /// readAndUpdateRole - read and update permission for col1 and no policy.
        /// </summary>
        [TestMethod]
        public void TestRoleAndOperationCombination()
        {
            const string READ_ONLY_ROLE = "readOnlyRole";
            const string READ_AND_UPDATE_ROLE = "readAndUpdateRole";

            EntityActionFields fieldsForRole = new(
                Include: new HashSet<string> { "col1" },
                Exclude: new());

            EntityAction readAction = new(
                Action: EntityActionOperation.Read,
                Fields: fieldsForRole,
                Policy: new(null, null));

            EntityAction updateAction = new(
                Action: EntityActionOperation.Update,
                Fields: fieldsForRole,
                Policy: new(null, null));

            EntityPermission readOnlyPermission = new(
                Role: READ_ONLY_ROLE,
                Actions: new[] { readAction });

            EntityPermission readAndUpdatePermission = new(
            Role: READ_AND_UPDATE_ROLE,
            Actions: new[] { readAction, updateAction });

            EntityPermission[] permissions = new EntityPermission[] { readOnlyPermission, readAndUpdatePermission };

            RuntimeConfig runtimeConfig = BuildTestRuntimeConfig(permissions, TEST_ENTITY);

            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            // Verify that read only role has permission for read and nothing else.
            Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                READ_ONLY_ROLE,
                EntityActionOperation.Read));
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                READ_ONLY_ROLE,
                EntityActionOperation.Update));
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                READ_ONLY_ROLE,
                EntityActionOperation.Create));
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                READ_ONLY_ROLE,
                EntityActionOperation.Delete));

            // Verify that read only role has permission for read/update and nothing else.
            Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                READ_AND_UPDATE_ROLE,
                EntityActionOperation.Read));
            Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                READ_AND_UPDATE_ROLE,
                EntityActionOperation.Update));
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                READ_AND_UPDATE_ROLE,
                EntityActionOperation.Create));
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                READ_AND_UPDATE_ROLE,
                EntityActionOperation.Delete));

            List<string> expectedRolesForRead = new() { READ_ONLY_ROLE, READ_AND_UPDATE_ROLE };
            List<string> expectedRolesForUpdate = new() { READ_AND_UPDATE_ROLE };

            IEnumerable<string> actualReadRolesForCol1 = authZResolver.GetRolesForField(
                AuthorizationHelpers.TEST_ENTITY,
                "col1",
                EntityActionOperation.Read);
            CollectionAssert.AreEquivalent(expectedRolesForRead, actualReadRolesForCol1.ToList());
            IEnumerable<string> actualUpdateRolesForCol1 = authZResolver.GetRolesForField(
                AuthorizationHelpers.TEST_ENTITY,
                "col1",
                EntityActionOperation.Update);
            CollectionAssert.AreEquivalent(expectedRolesForUpdate, actualUpdateRolesForCol1.ToList());

            IEnumerable<string> actualRolesForRead = IAuthorizationResolver.GetRolesForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                EntityActionOperation.Read,
                authZResolver.EntityPermissionsMap);
            CollectionAssert.AreEquivalent(expectedRolesForRead, actualRolesForRead.ToList());
            IEnumerable<string> actualRolesForUpdate = IAuthorizationResolver.GetRolesForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                EntityActionOperation.Update,
                authZResolver.EntityPermissionsMap);
            CollectionAssert.AreEquivalent(expectedRolesForUpdate, actualRolesForUpdate.ToList());
        }

        /// <summary>
        /// Test to validate that the permissions for the system role "authenticated" are derived the permissions of
        /// the system role "anonymous" when authenticated role is not defined, but anonymous role is defined.
        /// </summary>
        [TestMethod]
        public void TestAuthenticatedRoleWhenAnonymousRoleIsDefined()
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationResolver.ROLE_ANONYMOUS,
                operation: EntityActionOperation.Create);

            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            foreach (EntityActionOperation operation in EntityAction.ValidPermissionOperations)
            {
                if (operation is EntityActionOperation.Create)
                {
                    // Create operation should be defined for anonymous role.
                    Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(
                        AuthorizationHelpers.TEST_ENTITY,
                        AuthorizationResolver.ROLE_ANONYMOUS,
                        operation));

                    // Create operation should be defined for authenticated role as well,
                    // because it is defined for anonymous role.
                    Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(
                        AuthorizationHelpers.TEST_ENTITY,
                        AuthorizationResolver.ROLE_AUTHENTICATED,
                        operation));
                }
                else
                {
                    // Check that no other operation is defined for the authenticated role to ensure
                    // the authenticated role's permissions match that of the anonymous role's permissions.
                    Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(
                        AuthorizationHelpers.TEST_ENTITY,
                        AuthorizationResolver.ROLE_AUTHENTICATED,
                        operation));
                    Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(
                        AuthorizationHelpers.TEST_ENTITY,
                        AuthorizationResolver.ROLE_ANONYMOUS,
                        operation));
                }
            }

            // Anonymous role's permissions are copied over for authenticated role only.
            // Assert by checking for an arbitrary role.
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE, EntityActionOperation.Create));

            // Assert that the create operation has both anonymous, authenticated roles.
            List<string> expectedRolesForCreate = new() { AuthorizationResolver.ROLE_AUTHENTICATED, AuthorizationResolver.ROLE_ANONYMOUS };
            IEnumerable<string> actualRolesForCreate = IAuthorizationResolver.GetRolesForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                EntityActionOperation.Create,
                authZResolver.EntityPermissionsMap);
            CollectionAssert.AreEquivalent(expectedRolesForCreate, actualRolesForCreate.ToList());

            // Assert that the col1 field with create operation has both anonymous, authenticated roles.
            List<string> expectedRolesForCreateCol1 = new() {
                AuthorizationResolver.ROLE_ANONYMOUS,
                AuthorizationResolver.ROLE_AUTHENTICATED };
            IEnumerable<string> actualRolesForCreateCol1 = authZResolver.GetRolesForField(
                AuthorizationHelpers.TEST_ENTITY,
                "col1", EntityActionOperation.Create);
            CollectionAssert.AreEquivalent(expectedRolesForCreateCol1, actualRolesForCreateCol1.ToList());

            // Assert that the col1 field with read operation has no role.
            List<string> expectedRolesForReadCol1 = new();
            IEnumerable<string> actualRolesForReadCol1 = authZResolver.GetRolesForField(
                AuthorizationHelpers.TEST_ENTITY,
                "col1", EntityActionOperation.Read);
            CollectionAssert.AreEquivalent(expectedRolesForReadCol1, actualRolesForReadCol1.ToList());
        }

        /// <summary>
        /// Test to validate that the no permissions for authenticated role are derived when
        /// both anonymous and authenticated role are not defined.
        /// </summary>
        [TestMethod]
        public void TestAuthenticatedRoleWhenAnonymousRoleIsNotDefined()
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create);

            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            // Create operation should be defined for test role.
            Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.Create));

            // Create operation should not be defined for authenticated role,
            // because neither authenticated nor anonymous role is defined.
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationResolver.ROLE_AUTHENTICATED,
                EntityActionOperation.Create));

            // Assert that the Create operation has only test_role.
            List<string> expectedRolesForCreate = new() { AuthorizationHelpers.TEST_ROLE };
            IEnumerable<string> actualRolesForCreate = IAuthorizationResolver.GetRolesForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                EntityActionOperation.Create,
                authZResolver.EntityPermissionsMap);
            CollectionAssert.AreEquivalent(expectedRolesForCreate, actualRolesForCreate.ToList());

            // Since neither anonymous nor authenticated role is defined for the entity,
            // Create operation would only have the test_role.
            List<string> expectedRolesForCreateCol1 = new() { AuthorizationHelpers.TEST_ROLE };
            IEnumerable<string> actualRolesForCreateCol1 = authZResolver.GetRolesForField(
                AuthorizationHelpers.TEST_ENTITY,
                "col1", EntityActionOperation.Create);
            CollectionAssert.AreEquivalent(expectedRolesForCreateCol1, actualRolesForCreateCol1.ToList());
        }

        /// <summary>
        /// Test to validate that when anonymous and authenticated role are both defined, then
        /// the authenticated role does not derive permissions from anonymous role's permissions.
        /// </summary>
        [TestMethod]
        public void TestAuthenticatedRoleWhenBothAnonymousAndAuthenticatedAreDefined()
        {
            EntityActionFields fieldsForRole = new(
                Include: new HashSet<string> { "col1" },
                Exclude: new());

            EntityAction readAction = new(
                Action: EntityActionOperation.Read,
                Fields: fieldsForRole,
                Policy: new());

            EntityAction updateAction = new(
                Action: EntityActionOperation.Update,
                Fields: fieldsForRole,
                Policy: new());

            EntityPermission authenticatedPermission = new(
                Role: AuthorizationResolver.ROLE_AUTHENTICATED,
                Actions: new[] { readAction });

            EntityPermission anonymousPermission = new(
            Role: AuthorizationResolver.ROLE_ANONYMOUS,
            Actions: new[] { readAction, updateAction });

            EntityPermission[] permissions = new EntityPermission[] { authenticatedPermission, anonymousPermission };
            const string entityName = TEST_ENTITY;
            RuntimeConfig runtimeConfig = BuildTestRuntimeConfig(permissions, entityName);

            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            // Assert that for the role authenticated, only the Read operation is allowed.
            // The Update operation is not allowed even though update is allowed for the role anonymous.
            Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(AuthorizationHelpers.TEST_ENTITY,
                AuthorizationResolver.ROLE_AUTHENTICATED, EntityActionOperation.Read));
            Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(AuthorizationHelpers.TEST_ENTITY,
                AuthorizationResolver.ROLE_ANONYMOUS, EntityActionOperation.Update));
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(AuthorizationHelpers.TEST_ENTITY,
                AuthorizationResolver.ROLE_AUTHENTICATED, EntityActionOperation.Delete));

            // Assert that the read operation has both anonymous and authenticated role.
            List<string> expectedRolesForRead = new() {
                AuthorizationResolver.ROLE_ANONYMOUS,
                AuthorizationResolver.ROLE_AUTHENTICATED };
            IEnumerable<string> actualRolesForRead = IAuthorizationResolver.GetRolesForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                EntityActionOperation.Read,
                authZResolver.EntityPermissionsMap);
            CollectionAssert.AreEquivalent(expectedRolesForRead, actualRolesForRead.ToList());

            // Assert that the update operation has only anonymous role.
            List<string> expectedRolesForUpdate = new() { AuthorizationResolver.ROLE_ANONYMOUS };
            IEnumerable<string> actualRolesForUpdate = IAuthorizationResolver.GetRolesForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                EntityActionOperation.Update,
                authZResolver.EntityPermissionsMap);
            CollectionAssert.AreEquivalent(expectedRolesForUpdate, actualRolesForUpdate.ToList());

            // Assert that the col1 field with Read operation has both anonymous and authenticated roles.
            List<string> expectedRolesForReadCol1 = new() {
                AuthorizationResolver.ROLE_ANONYMOUS,
                AuthorizationResolver.ROLE_AUTHENTICATED };
            IEnumerable<string> actualRolesForReadCol1 = authZResolver.GetRolesForField(
                AuthorizationHelpers.TEST_ENTITY,
                "col1", EntityActionOperation.Read);
            CollectionAssert.AreEquivalent(expectedRolesForReadCol1, actualRolesForReadCol1.ToList());

            // Assert that the col1 field with Update operation has only anonymous roles.
            List<string> expectedRolesForUpdateCol1 = new() { AuthorizationResolver.ROLE_ANONYMOUS };
            IEnumerable<string> actualRolesForUpdateCol1 = authZResolver.GetRolesForField(
                AuthorizationHelpers.TEST_ENTITY,
                "col1", EntityActionOperation.Update);
            CollectionAssert.AreEquivalent(expectedRolesForUpdateCol1, actualRolesForUpdateCol1.ToList());
        }

        /// <summary>
        /// Test to validate the AreRoleAndOperationDefinedForEntity method for the case insensitivity of roleName.
        /// For eg. The role Writer is equivalent to wrIter, wRITer, WRITER etc.
        /// </summary>
        /// <param name="configRole">The role configured on the entity.</param>
        /// <param name="operation">The operation configured for the configRole.</param>
        /// <param name="roleNameToCheck">The roleName which is to be checked for the permission.</param>
        [DataTestMethod]
        [DataRow("Writer", EntityActionOperation.Create, "wRiTeR", DisplayName = "role wRiTeR checked against Writer")]
        [DataRow("Reader", EntityActionOperation.Read, "READER", DisplayName = "role READER checked against Reader")]
        [DataRow("Writer", EntityActionOperation.Create, "WrIter", DisplayName = "role WrIter checked against Writer")]
        public void AreRoleAndOperationDefinedForEntityTestForDifferentlyCasedRole(
            string configRole,
            EntityActionOperation operation,
            string roleNameToCheck
            )
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: configRole,
                operation: operation);
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            // Assert that the roleName is case insensitive.
            Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(AuthorizationHelpers.TEST_ENTITY, roleNameToCheck, operation));
        }
        #endregion

        #region Column Tests

        /// <summary>
        /// Tests the authorization stage: Columns defined for operation
        /// Columns are allowed for role
        /// Columns are not allowed for role
        /// Wildcard included and/or excluded columns handling
        /// and assumes request validation has already occurred
        /// </summary>
        [TestMethod("Explicit include columns with no exclusion")]
        public void ExplicitIncludeColumn()
        {
            HashSet<string> includedColumns = new() { "col1", "col2", "col3" };
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedCols: includedColumns
                );
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Assert.IsTrue(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.Create,
                includedColumns));

            // Not allow column.
            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.Create,
                new List<string> { "col4" }));

            // Mix of allow and not allow. Should result in not allow.
            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.Create,
                new List<string> { "col3", "col4" }));

            // Column does not exist 
            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.Create,
                new List<string> { "col5", "col6" }));
        }

        /// <summary>
        /// Test to validate that for wildcard operation, the authorization stage for column check
        /// would pass if the operation is one among create, read, update, delete and the columns are accessible.
        /// Similarly if the column is in accessible, then we should not have access.
        /// </summary>
        [TestMethod("Explicit include and exclude columns")]
        public void ExplicitIncludeAndExcludeColumns()
        {
            HashSet<string> includeColumns = new() { "col1", "col2" };
            HashSet<string> excludeColumns = new() { "col3" };

            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedCols: includeColumns,
                excludedCols: excludeColumns
                );

            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Assert.IsTrue(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.Create,
                includeColumns));

            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.Create,
                excludeColumns));

            // Not exist column in the inclusion or exclusion list
            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.Create,
                new List<string> { "col4" }));

            // Mix of allow and not allow. Should result in not allow.
            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.Create,
                new List<string> { "col1", "col3" }));
        }

        /// <summary>
        /// Exclusion has precedence over inclusion. So for this test case,
        /// col1 will be excluded even if it is in the inclusion list.
        /// </summary>
        [TestMethod("Same column in exclusion and inclusion list")]
        public void ColumnExclusionWithSameColumnInclusion()
        {
            HashSet<string> includedColumns = new() { "col1", "col2" };
            HashSet<string> excludedColumns = new() { "col1", "col4" };

            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedCols: includedColumns,
                excludedCols: excludedColumns
                );
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            // Col2 should be included.
            //
            Assert.IsTrue(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                new List<string> { "col2" }));

            // Col1 should NOT to included since it is in exclusion list.
            //
            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                new List<string> { "col1" }));

            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                excludedColumns));
        }

        /// <summary>
        /// Test that wildcard inclusion will include all the columns in the table.
        /// </summary>
        [TestMethod("Wildcard included columns")]
        public void WildcardColumnInclusion()
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedCols: new HashSet<string> { AuthorizationResolver.WILDCARD }
                );
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            List<string> includedColumns = new() { "col1", "col2", "col3", "col4" };

            Assert.IsTrue(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedColumns));
        }

        /// <summary>
        /// Test that wildcard inclusion will include all column except column specify in exclusion.
        /// Exclusion has priority over inclusion.
        /// </summary>
        [TestMethod("Wildcard include columns with some column exclusion")]
        public void WildcardColumnInclusionWithExplicitExclusion()
        {
            List<string> includedColumns = new() { "col1", "col2" };
            HashSet<string> excludedColumns = new() { "col3", "col4" };

            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedCols: new HashSet<string> { AuthorizationResolver.WILDCARD },
                excludedCols: excludedColumns
                );
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Assert.IsTrue(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedColumns));
            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                excludedColumns));
        }

        /// <summary>
        /// Test that all columns should be excluded if the exclusion contains wildcard character.
        /// </summary>
        [TestMethod("Wildcard column exclusion")]
        public void WildcardColumnExclusion()
        {
            HashSet<string> excludedColumns = new() { "col1", "col2", "col3", "col4" };

            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                excludedCols: new HashSet<string> { AuthorizationResolver.WILDCARD }
                );
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                excludedColumns));
        }

        /// <summary>
        /// For this test, exclusion has precedence over inclusion. So all columns will be excluded
        /// because wildcard is specified in the exclusion list.
        /// </summary>
        [TestMethod("Wildcard column exclusion with some explicit columns inclusion")]
        public void WildcardColumnExclusionWithExplicitColumnInclusion()
        {
            HashSet<string> includedColumns = new() { "col1", "col2" };
            HashSet<string> excludedColumns = new() { "col3", "col4" };

            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedCols: includedColumns,
                excludedCols: new HashSet<string> { AuthorizationResolver.WILDCARD }
                );
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedColumns));
            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                excludedColumns));
        }

        /// <summary>
        /// Test to validate that for wildcard operation, the authorization stage for column check
        /// would pass if the operation is one among create, read, update, delete and the columns are accessible.
        /// Similarly if the column is in accessible, then we should not have access.
        /// </summary>
        [TestMethod("Explicit include and exclude columns with wildcard operation")]
        public void CheckIncludeAndExcludeColumnForWildcardOperation()
        {
            HashSet<string> includeColumns = new() { "col1", "col2" };
            HashSet<string> excludeColumns = new() { "col3" };

            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.All,
                includedCols: includeColumns,
                excludedCols: excludeColumns
                );

            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            foreach (EntityActionOperation operation in EntityAction.ValidPermissionOperations)
            {
                // Validate that the authorization check passes for valid CRUD operations
                // because columns are accessible or inaccessible.
                Assert.IsTrue(authZResolver.AreColumnsAllowedForOperation(
                    AuthorizationHelpers.TEST_ENTITY,
                    AuthorizationHelpers.TEST_ROLE,
                    operation,
                    includeColumns));
                Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                    AuthorizationHelpers.TEST_ENTITY,
                    AuthorizationHelpers.TEST_ROLE,
                    operation,
                    excludeColumns));
            }
        }

        /// <summary>
        /// Test to validate that when Field property is missing from the operation, all the columns present in
        /// the table are treated as accessible. Since we are not explicitly specifying the includeCols/excludedCols
        /// parameters when initializing the RuntimeConfig, Field will be nullified.
        /// </summary>
        [DataTestMethod]
        [DataRow(true, "col1", "col2", DisplayName = "Accessible fields col1,col2")]
        [DataRow(true, "col3", "col4", DisplayName = "Accessible fields col3,col4")]
        [DataRow(false, "col5", DisplayName = "Inaccessible field col5")]
        public void AreColumnsAllowedForOperationWithMissingFieldProperty(bool expected, params string[] columnsToCheck)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create
            );
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            // Assert that the expected result and the returned result are equal.
            // The entity is expected to have "col1", "col2", "col3", "col4" fields accessible on it.
            Assert.AreEqual(expected,
                authZResolver.AreColumnsAllowedForOperation(
                    AuthorizationHelpers.TEST_ENTITY,
                    AuthorizationHelpers.TEST_ROLE,
                    EntityActionOperation.Create,
                    new List<string>(columnsToCheck)));
        }

        /// <summary>
        /// Test to validate the AuthorizationResolver.GetAllAuthenticatedUserClaims() successfully adds role claim to the resolvedClaims dictionary.
        /// Only one "roles" claim is added to the dictionary resolvedClaims and only one Claim is added to the list maintained
        /// for the key "roles" where the value corresponds to the X-MS-API-ROLE header.
        /// e.g [key: "roles"] -> [value: List (Claim) { Claim(type: "roles", value: "{x-ms-api-role value") }]
        /// The role claim will be sourced by DAB when the user is not already a member of a system role(authenticated/anonymous),
        /// or the role claim will be sourced from a user's access token issued by an identity provider.
        /// </summary>
        [TestMethod]
        public void ValidateClientRoleHeaderClaimIsAddedToResolvedClaims()
        {
            Mock<HttpContext> context = new();

            //Add identity to the request context.
            ClaimsIdentity identityWithClientRoleHeaderClaim = new(TEST_AUTHENTICATION_TYPE, TEST_CLAIMTYPE_NAME, AuthenticationOptions.ROLE_CLAIM_TYPE);
            Claim clientRoleHeaderClaim = new(AuthenticationOptions.ROLE_CLAIM_TYPE, TEST_ROLE);
            identityWithClientRoleHeaderClaim.AddClaim(clientRoleHeaderClaim);

            // Add identity to the request context.
            ClaimsIdentity identityWithoutClientRoleHeaderClaim = new(TEST_AUTHENTICATION_TYPE, TEST_CLAIMTYPE_NAME, AuthenticationOptions.ROLE_CLAIM_TYPE);
            Claim readerRoleClaim = new(AuthenticationOptions.ROLE_CLAIM_TYPE, "Reader");
            identityWithClientRoleHeaderClaim.AddClaim(readerRoleClaim);

            ClaimsPrincipal principal = new();
            principal.AddIdentity(identityWithoutClientRoleHeaderClaim);
            principal.AddIdentity(identityWithClientRoleHeaderClaim);

            context.Setup(x => x.User).Returns(principal);
            context.Setup(x => x.Request.Headers[AuthorizationResolver.CLIENT_ROLE_HEADER]).Returns(TEST_ROLE);

            // Execute the method to be tested - GetAllUserClaims().
            Dictionary<string, List<Claim>> resolvedClaims = AuthorizationResolver.GetAllAuthenticatedUserClaims(context.Object);

            // Assert that only the role claim corresponding to clientRoleHeader is added to the claims dictionary.
            // Assert
            Assert.AreEqual(resolvedClaims.ContainsKey(AuthenticationOptions.ROLE_CLAIM_TYPE), true, message: "Only the claim, roles, should be present.");
            Assert.AreEqual(resolvedClaims[AuthenticationOptions.ROLE_CLAIM_TYPE].Count, 1, message: "Only one claim should be present to represent the client role header context.");
            Assert.AreEqual(resolvedClaims[AuthenticationOptions.ROLE_CLAIM_TYPE].First().Value, TEST_ROLE, message: "The roles claim should have the value:" + TEST_ROLE);
        }

        /// <summary>
        /// Validates that AuthorizationResolver.GetProcessedUserClaims(...)
        /// -> returns a dictionary (string, string) where each key is a claim's name and each value is a claim's value.
        /// -> Resolves scope/scp claims to a single string value with scopes delimited by spaces.
        /// -> Resolves multiple instances of a claim into a JSON array mirroring the format
        /// of the claim in the original JWT token. The JSON array's type depends on the value type
        /// present in the original JWT token.
        /// </summary>
        [TestMethod]
        public async Task TestClaimsParsingToJson()
        {
            // Arrange
            Dictionary<string, object> distributedClaimSources = new()
            {
                ["src1"] = new { endpoint = "https://graph.microsoft.com/v1.0/users/{userID}/getMemberObjects" }
            };

            // Without this change, newest Microsoft.IdentityModel (8+) package will throw an error:
            // System.ArgumentException: IDX11025: Cannot serialize object of type: '<>f__AnonymousType0`1[System.String]' into property: 'src1'.
            // https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/issues/2397
            JsonElement serializedDistributedClaims = JsonSerializer.SerializeToElement(distributedClaimSources);

            Dictionary<string, object?> claimsCollection = new()
            {
                { "scp", "scope1 scope2 scope3" },
                { "groups", "src1" },
                { "_claim_sources", serializedDistributedClaims },
                { "wids", new List<string>() { "d74b8d81-39eb-4201-bd9f-9f1c4011e3c9", "18d14519-c4da-4ad4-936d-9a2de69d33cf", "9e513fc0-e8af-43b1-a6c7-949edb1967a3" } },
                { "int_array", new List<int>() { 1, 2, 3 } },
                { "bool_array", new List<bool>() { true, false, true } },
                { "roles", new List<string> { "anonymous", "authenticated", TEST_ROLE } },
                { "nullValuedClaim",  null}
            };

            ClaimsPrincipal principal = await CreateClaimsPrincipal(userClaims: claimsCollection);

            Mock<HttpContext> context = new();
            context.Setup(x => x.User).Returns(principal);
            context.Setup(x => x.Request.Headers[AuthorizationResolver.CLIENT_ROLE_HEADER]).Returns(TEST_ROLE);

            // Act
            Dictionary<string, string> claimsInRequestContext = AuthorizationResolver.GetProcessedUserClaims(context.Object);

            // Assert
            // The only role that should be present is the role set in the client role header. The claims collection
            // configured in the 'arrange' section of this test must include that role in the list of role claims.
            // DAB historically only sent a single role into MS SQL's session context, so changing this behavior
            // is a breaking change.
            Assert.AreEqual(expected: true, claimsInRequestContext.ContainsKey(AuthenticationOptions.ROLE_CLAIM_TYPE), message: "The 'roles' claim must be present.");
            Assert.AreEqual(expected: TEST_ROLE, claimsInRequestContext[AuthenticationOptions.ROLE_CLAIM_TYPE], message: "The roles claim should have the value:" + TEST_ROLE);

            // Ensure JSON arrays are reconstructed correctly aligning to the JWT token claim's value type
            Assert.AreEqual(expected: @"[""d74b8d81-39eb-4201-bd9f-9f1c4011e3c9"",""18d14519-c4da-4ad4-936d-9a2de69d33cf"",""9e513fc0-e8af-43b1-a6c7-949edb1967a3""]", claimsInRequestContext["wids"]);
            Assert.AreEqual(expected: "[1,2,3]", actual: claimsInRequestContext["int_array"]);
            Assert.AreEqual(expected: "[true,false,true]", actual: claimsInRequestContext["bool_array"]);
            Assert.AreEqual(expected: @"src1", actual: claimsInRequestContext["groups"]);
            Assert.AreEqual(expected: @"{""src1"":{""endpoint"":""https://graph.microsoft.com/v1.0/users/{userID}/getMemberObjects""}}", actual: claimsInRequestContext["_claim_sources"]);
            Assert.AreEqual(expected: "1706816426", actual: claimsInRequestContext["iat"]);
            Assert.AreEqual(expected: "", actual: claimsInRequestContext["nullValuedClaim"]);
        }

        /// <summary>
        /// JWT token JSON payloads may not be flat and may contain nested JSON objects or arrays.
        /// This test validates that when dotnet's JWT processing code flattens the JWT token payload
        /// into Claim objects, the AuthorizationResolver.GetProcessedUserClaims(...) method correctly
        /// returns a dictionary where key=claimType and value= scalar value or serialized JSON array.
        /// This test enforces that DAB only processes one 'roles' claim whose value matches the x-ms-api-role
        /// header, which protects against regression.
        /// </summary>
        [TestMethod]
        public void UniqueClaimsResolvedForDbPolicy_SessionCtx_Usage()
        {
            // Arrange
            Mock<HttpContext> context = new();

            // Creat list of claims for testing
            List<Claim> claims = new()
            {
                new("scp", "openid"),
                new("sub", "Aa_0RISCzzZ-abC1De2fGHIjKLMNo123pQ4rStUVWXY"),
                new("oid", "55296aad-ea7f-4c44-9a4c-bb1e8d43a005"),
                new(AuthenticationOptions.ROLE_CLAIM_TYPE, TEST_ROLE),
                new(AuthenticationOptions.ROLE_CLAIM_TYPE, "ROLE2")
            };

            //Add identity object to the Mock context object.
            ClaimsIdentity identityWithClientRoleHeaderClaim = new(TEST_AUTHENTICATION_TYPE, TEST_CLAIMTYPE_NAME, AuthenticationOptions.ROLE_CLAIM_TYPE);
            identityWithClientRoleHeaderClaim.AddClaims(claims);

            ClaimsPrincipal principal = new();
            principal.AddIdentity(identityWithClientRoleHeaderClaim);

            context.Setup(x => x.User).Returns(principal);
            context.Setup(x => x.Request.Headers[AuthorizationResolver.CLIENT_ROLE_HEADER]).Returns(TEST_ROLE);

            // Act
            Dictionary<string, string> claimsInRequestContext = AuthorizationResolver.GetProcessedUserClaims(context.Object);

            // Assert
            Assert.AreEqual(expected: claims.Count - 1, actual: claimsInRequestContext.Count, message: "Unexpected number of claims. Expected a count of unique claim types.");
            Assert.AreEqual(expected: "openid", actual: claimsInRequestContext["scp"], message: "Expected the scp claim to be present.");
            Assert.AreEqual(expected: "Aa_0RISCzzZ-abC1De2fGHIjKLMNo123pQ4rStUVWXY", actual: claimsInRequestContext["sub"], message: "Expected the sub claim to be present.");
            Assert.AreEqual(expected: "55296aad-ea7f-4c44-9a4c-bb1e8d43a005", actual: claimsInRequestContext["oid"], message: "Expected the oid claim to be present.");
            Assert.AreEqual(claimsInRequestContext[AuthenticationOptions.ROLE_CLAIM_TYPE], actual: TEST_ROLE, message: "The roles claim should have the value:" + TEST_ROLE);
        }

        /// <summary>
        /// Validates that AuthorizationResolver.GetProcessedUserClaims(httpContext) does not resolve claims sourced from
        /// an unauthenticated ClaimsIdentity object within a ClaimsPrincipal.
        /// While no scenarios currently inject an unauthenticated ClaimsIdentity object into a ClaimsPrincipal,
        /// any scenarios that do so will have their unauthenticated ClaimsIdentity claims ignored.
        /// </summary>
        [TestMethod]
        public void ValidateUnauthenticatedUserClaimsAreNotResolvedWhenProcessingUserClaims()
        {
            // Arrange
            Mock<HttpContext> context = new();

            // Create authenticated ClaimsIdentity object with list of claims.
            List<Claim> authenticatedUserclaims = new()
            {
                new("scp", "openid", ClaimValueTypes.String),
                new("oid", "55296aad-ea7f-4c44-9a4c-bb1e8d43a005", ClaimValueTypes.String),
                new(AuthenticationOptions.ROLE_CLAIM_TYPE, TEST_ROLE, ClaimValueTypes.String),
            };

            //Add identity object to the Mock context object.
            ClaimsIdentity authenticatedIdentity = new(TEST_AUTHENTICATION_TYPE, TEST_CLAIMTYPE_NAME, AuthenticationOptions.ROLE_CLAIM_TYPE);
            authenticatedIdentity.AddClaims(authenticatedUserclaims);

            // Create unauthenticated ClaimsIdentity object with list of claims.
            List<Claim> unauthenticatedClaims = new()
            {
                new("scp", "invalidScope", ClaimValueTypes.String),
                new("oid", "1337", ClaimValueTypes.String),
                new(AuthenticationOptions.ROLE_CLAIM_TYPE, "Don't_Parse_This_Role",ClaimValueTypes.String)
            };

            // ClaimsIdentity is unauthenticated because the authenticationType is null.
            ClaimsIdentity unauthenticatedIdentity = new(authenticationType: null, TEST_CLAIMTYPE_NAME, AuthenticationOptions.ROLE_CLAIM_TYPE);
            unauthenticatedIdentity.AddClaims(unauthenticatedClaims);

            //Add identities object to the Mock context object.
            ClaimsPrincipal principal = new();
            principal.AddIdentity(authenticatedIdentity);
            principal.AddIdentity(unauthenticatedIdentity);

            context.Setup(x => x.User).Returns(principal);
            context.Setup(x => x.Request.Headers[AuthorizationResolver.CLIENT_ROLE_HEADER]).Returns(TEST_ROLE);

            // Act
            Dictionary<string, string> resolvedClaims = AuthorizationResolver.GetProcessedUserClaims(context.Object);

            // Assert
            Assert.AreEqual(expected: authenticatedUserclaims.Count, actual: resolvedClaims.Count, message: "Only two claims should be present.");
            Assert.AreEqual(expected: "openid", actual: resolvedClaims["scp"], message: "Unexpected scp claim returned.");

            bool didResolveUnauthenticatedRoleClaim = resolvedClaims[AuthenticationOptions.ROLE_CLAIM_TYPE] == "Don't_Parse_This_Role";
            Assert.AreEqual(expected: false, actual: didResolveUnauthenticatedRoleClaim, message: "Unauthenticated roles claim erroneously resolved.");

            bool didResolveUnauthenticatedOidClaim = resolvedClaims["oid"] == "1337";
            Assert.AreEqual(expected: false, actual: didResolveUnauthenticatedRoleClaim, message: "Unauthenticated oid claim erroneously resolved.");
        }
        #endregion

        #region Helpers
        /// <summary>
        /// Creates code-first in-memory RuntimeConfig.
        /// </summary>
        /// <param name="entityName">Name of the entity.</param>
        /// <param name="roleName">Role permitted to access entity.</param>
        /// <param name="operation">Operation to allow on role</param>
        /// <param name="includedCols">Allowed columns to access for operation defined on role.</param>
        /// <param name="excludedCols">Excluded columns to access for operation defined on role.</param>
        /// <param name="requestPolicy">Request authorization policy. (Support TBD)</param>
        /// <param name="databasePolicy">Database authorization policy.</param>
        /// <returns>Mocked RuntimeConfig containing metadata provided in method arguments.</returns>
        private static RuntimeConfig InitRuntimeConfig(
            string entityName = "SampleEntity",
            string roleName = "Reader",
            EntityActionOperation operation = EntityActionOperation.Create,
            HashSet<string>? includedCols = null,
            HashSet<string>? excludedCols = null,
            string? requestPolicy = null,
            string? databasePolicy = null
            )
        {
            EntityActionFields fieldsForRole = new(
                Include: includedCols,
                Exclude: excludedCols ?? new());

            EntityActionPolicy policy = new(
                    Request: requestPolicy,
                    Database: databasePolicy);

            EntityAction operationForRole = new(
                Action: operation,
                Fields: fieldsForRole,
                Policy: policy);

            EntityPermission permissionForEntity = new(
                Role: roleName,
                Actions: new[] { operationForRole });

            return BuildTestRuntimeConfig(new[] { permissionForEntity }, entityName);
        }

        private static RuntimeConfig BuildTestRuntimeConfig(EntityPermission[] permissions, string entityName)
        {
            Entity sampleEntity = new(
                Source: new(entityName, EntitySourceType.Table, null, null),
                Rest: new(Enabled: true),
                GraphQL: new("", ""),
                Permissions: permissions,
                Relationships: null,
                Mappings: null);

            Dictionary<string, Entity> entityMap = new()
            {
                { entityName, sampleEntity }
            };

            RuntimeConfig runtimeConfig = new(
                Schema: "UnitTestSchema",
                DataSource: new DataSource(DatabaseType: DatabaseType.MSSQL, "", new()),
                Runtime: new(
                    Rest: new(),
                    GraphQL: new(),
                    Mcp: null,
                    Host: new(null, null)
                ),
                Entities: new(entityMap)
            );
            return runtimeConfig;
        }

        /// <summary>
        /// Returns a ClaimsPrincipal object created from a jwt token which uses either the provided
        /// claimsCollection or a default claimsCollection capturing various value types of possible claims.
        /// Value types include:
        /// - space delimited string for scp claim
        /// - JSON String array (roles)
        /// - JSON object (distributed groups claim)
        /// - JSON int/bool array (theoretical, though not in EntraID access token)
        /// </summary>
        /// <param name="userClaims">Test configured collection of claims to include in ClientPrincipal</param>
        /// <returns>Authenticated ClaimsPrincipal with user specified or default claims</returns>
        private async static Task<ClaimsPrincipal> CreateClaimsPrincipal(Dictionary<string, object?>? userClaims = null)
        {
            if (userClaims is null)
            {
                Dictionary<string, object> distributedClaimSources = new()
                {
                    ["src1"] = new { endpoint = "https://graph.microsoft.com/v1.0/users/{userID}/getMemberObjects" }
                };

                userClaims = new()
                {
                    { "scope", "scope1 scope2 scope3" },
                    { "groups", "src1" },
                    { "_claim_sources", distributedClaimSources},
                    { "wids", new List<string>() { "d74b8d81-39eb-4201-bd9f-9f1c4011e3c9", "18d14519-c4da-4ad4-936d-9a2de69d33cf", "9e513fc0-e8af-43b1-a6c7-949edb1967a3" } },
                    { "int_array", new List<int>() { 1, 2, 3 } },
                    { "bool_array", new List<bool>() { true, false, true } },
                    { "roles", new List<string> { "anonymous", "authenticated", TEST_ROLE } },
                    { "nullValuedClaim",  null}
                };
            }

            RsaSecurityKey signingKey = new(RSA.Create(2048));
            SecurityTokenDescriptor tokenDescriptor = new()
            {
                Audience = "d727a7e8-1af4-4ce0-8c56-f3107f10bbfd",
                Issuer = "https://fabrikam.com",
                Claims = userClaims,
                NotBefore = DateTime.Now.AddHours(-1),
                Expires = DateTime.Now.AddHours(1),
                SigningCredentials = new(key: signingKey, algorithm: SecurityAlgorithms.RsaSsaPssSha256)
            };

            JsonWebTokenHandler tokenHandler = new();
            string jwtToken = tokenHandler.CreateToken(tokenDescriptor);
            HttpContext context = await WebHostBuilderHelper.SendRequestAndGetHttpContextState(key: signingKey, token: jwtToken);
            return context.User;
        }
        #endregion
    }
}
