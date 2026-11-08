namespace Packmoat.Infrastructure.Tests;

/// <summary><see cref="TenantPurgeStoreContract"/> on SQLite (#288 P2). The tenant purge's store's hosted tests (the hosted module's parts) stay in the hosted test project.</summary>
[Collection("sqlite-pools")]
public class TenantPurgeStoreTests : TenantPurgeStoreContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;
}
