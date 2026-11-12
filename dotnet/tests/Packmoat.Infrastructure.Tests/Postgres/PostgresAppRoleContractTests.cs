using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

// The whole PostgreSQL contract suite again, signed in as the app role: each fixture below is a PostgreSQL contract
// fixture with every one of its tests (the contract's and its own), on databases the owner role migrated with db migrate --grant-to, and
// with the stores signing in as the app role, as the server and the day-to-day command line will (PostgresSqlEngine.AsAppRole). A store
// that needed more than SELECT, INSERT, UPDATE and DELETE (and its sequences) fails here. StoreContractCoverageTests holds every
// PostgreSQL fixture to have its twin here: PostgresAppRole<the rest of its name>, derived from it.

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleAdminActionLogTests : PostgresAdminActionLogTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleAuditRetentionStoreTests : PostgresAuditRetentionStoreTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleAuditTests : PostgresAuditTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleAutoJoinStoreTests : PostgresAutoJoinStoreTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleCookieKeyStoreTests : PostgresCookieKeyStoreTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleCustomerDirectoryTests : PostgresCustomerDirectoryTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleDecisionRecordQueryTests : PostgresDecisionRecordQueryTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleDecisionRecordStoreTests : PostgresDecisionRecordStoreTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleDeveloperCountingTests : PostgresDeveloperCountingTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleFeedCheckStoreTests : PostgresFeedCheckStoreTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleJobLeaseTests : PostgresJobLeaseTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleRateCounterTests : PostgresRateCounterTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleFormNonceStoreTests : PostgresFormNonceStoreTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleFeedSetupScratchTests : PostgresFeedSetupScratchTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleMasterKeyCheckTests : PostgresMasterKeyCheckTests
{
    protected override IMasterKeyEngine Engine => PostgresSqlEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleMemberTokenTests : PostgresMemberTokenTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleOrganisationStoreTests : PostgresOrganisationStoreTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleSchemaRulesTests : PostgresSchemaRulesTests
{
    protected override ISqlEngine Engine => PostgresSqlEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleSelfServiceFeedStoreTests : PostgresSelfServiceFeedStoreTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleFlaggedOperatorSelfServiceFeedStoreTests : PostgresFlaggedOperatorSelfServiceFeedStoreTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleSharedAppIdentityTests : PostgresSharedAppIdentityTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleTenantPurgeStoreTests : PostgresTenantPurgeStoreTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleTenantStoreTests : PostgresTenantStoreTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleTokenTests : PostgresTokenTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAppRoleWaiverStoreTests : PostgresWaiverStoreTests
{
    protected override IStoreEngine Engine => PostgresStoreEngine.AppRoleInstance;
}
