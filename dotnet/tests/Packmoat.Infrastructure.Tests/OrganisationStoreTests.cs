namespace Packmoat.Infrastructure.Tests;

/// <summary><see cref="OrganisationStoreContract"/> on SQLite (#288 P2). The organisation creation's hosted tests (the hosted module's parts) stay in the hosted test project.</summary>
[Collection("sqlite-pools")]
public class OrganisationStoreTests : OrganisationStoreContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;
}
