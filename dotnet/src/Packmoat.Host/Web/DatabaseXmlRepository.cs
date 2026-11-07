using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Packmoat.Application;

namespace Packmoat.Host.Web;

/// <summary>
/// The data protection key ring's storage on the database (#232 B1, PACKMOAT_COOKIE_KEYS=database): each key is one row of the cookie
/// key store, encrypted there with the master key. Never logs a key's XML.
/// </summary>
internal sealed class DatabaseXmlRepository(ICookieKeyStore store) : IXmlRepository
{
    // Sync over async is safe here: ASP.NET Core has no synchronization context to deadlock on, and the store does not need one.
    public IReadOnlyCollection<XElement> GetAllElements() =>
        store.GetAllAsync(CancellationToken.None).GetAwaiter().GetResult().Select(k => XElement.Parse(k.Xml)).ToList();

    public void StoreElement(XElement element, string friendlyName) =>
        store.AddAsync(friendlyName, element.ToString(SaveOptions.DisableFormatting), CancellationToken.None).GetAwaiter().GetResult();
}
