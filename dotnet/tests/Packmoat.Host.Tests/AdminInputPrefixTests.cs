using Packmoat.Application;
using Packmoat.Host.Admin;

namespace Packmoat.Host.Tests;

/// <summary>The admin site's path prefix field takes the wizard's longest tight prefix, so an operator can edit a connected tenant.</summary>
public class AdminInputPrefixTests
{
    [Fact]
    public void The_longest_tight_prefix_the_wizard_builds_is_accepted_as_it_is()
    {
        var prefix = new ResolvedFeed(new string('a', 50), Guid.NewGuid(), Guid.NewGuid()).PathPrefix;
        Assert.Equal(136, prefix.Length);   // 50 + 1 + 36 + 12 + 36 + 1
        Assert.Equal(prefix, AdminInput.AllowedPath(prefix, out var error));
        Assert.Null(error);
        var orgScoped = new ResolvedFeed(new string('a', 50), null, Guid.NewGuid()).PathPrefix;
        Assert.Equal(orgScoped, AdminInput.AllowedPath(orgScoped, out _));
    }

    [Fact]
    public void Up_to_199_characters_pass_and_200_are_refused()
    {
        var max = "a" + new string('b', 197) + "/";
        Assert.Equal(199, max.Length);
        Assert.Equal(max, AdminInput.AllowedPath(max, out _));

        var tooLong = "a" + new string('b', 198) + "/";
        Assert.Equal(200, tooLong.Length);
        Assert.Null(AdminInput.AllowedPath(tooLong, out var error));
        Assert.Equal("bad-path-prefix", error);
        Assert.Null(AdminInput.AllowedPath(new string('a', 200), out _));
    }
}
