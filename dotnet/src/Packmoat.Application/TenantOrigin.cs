namespace Packmoat.Application;

/// <summary>Who made a tenant: the operator (CLI or admin site) or a customer signing up for themselves.</summary>
public enum TenantOrigin { Operator, Self }
