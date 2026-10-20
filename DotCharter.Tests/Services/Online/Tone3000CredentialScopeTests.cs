using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tests.Services.Online;

/// <summary>
///     The model download URL arrives in an API RESPONSE — it is not a constant
///     we control. The download request carries the signed-in user's bearer
///     token, so attaching it unconditionally meant whatever host that response
///     named received a working credential for the user's TONE3000 account, and
///     <c>http</c> was accepted, putting the token on the wire in cleartext.
/// </summary>
public sealed class Tone3000CredentialScopeTests
{
    [Theory]
    [InlineData("https://www.tone3000.com/files/model.nam")]
    [InlineData("https://tone3000.com/files/model.nam")]
    [InlineData("https://cdn.tone3000.com/a/b.nam")]
    [InlineData("https://TONE3000.COM/x.nam")]
    public void TheTokenGoesToTone3000AndItsSubdomains(string url) => Assert.True(Tone3000Client.IsTrustedCredentialHost(new Uri(url)));

    [Theory]
    [InlineData("https://evil.example/model.nam")]
    [InlineData("https://nottone3000.com/model.nam")] // suffix match without the dot
    [InlineData("https://tone3000.com.evil.example/model.nam")] // prefix, not the real host
    [InlineData("https://evil.example/?x=tone3000.com")]
    public void TheTokenNeverGoesToAnyoneElse(string url) => Assert.False(Tone3000Client.IsTrustedCredentialHost(new Uri(url)));

    [Fact]
    public void PlainHttpNeverReceivesTheToken()
    {
        // Even the real host over http would expose the credential in transit.
        Assert.False(Tone3000Client.IsTrustedCredentialHost(new Uri("http://www.tone3000.com/model.nam")));
        Assert.False(Tone3000Client.IsTrustedCredentialHost(new Uri("http://tone3000.com/model.nam")));
    }
}
