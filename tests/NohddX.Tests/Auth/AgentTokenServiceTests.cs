using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NohddX.Api.Auth;
using NohddX.Core.Configuration;
using Xunit;

namespace NohddX.Tests.Auth;

public class AgentTokenServiceTests
{
    [Fact]
    public void Round_trip_token_validates()
    {
        var svc = MakeService("dGVzdC1zZWNyZXQta2V5LWFsd2F5cy0xMjM=");
        var agentId = Guid.NewGuid();

        var token = svc.IssueToken(agentId);
        var validated = svc.Validate(token);

        validated.Should().Be(agentId);
    }

    [Fact]
    public void Token_signed_by_different_secret_is_rejected()
    {
        var svcA = MakeService("c2VjcmV0LWE=");
        var svcB = MakeService("c2VjcmV0LWI=");

        var tok = svcA.IssueToken(Guid.NewGuid());
        svcB.Validate(tok).Should().BeNull();
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        var svc = MakeService("c2VjcmV0LWE=");
        var tok = svc.IssueToken(Guid.NewGuid());
        var parts = tok.Split('.');
        var tampered = $"{Guid.NewGuid():N}.{parts[1]}.{parts[2]}";

        svc.Validate(tampered).Should().BeNull();
    }

    [Fact]
    public void Malformed_token_returns_null_not_throw()
    {
        var svc = MakeService("c2VjcmV0LWE=");
        svc.Validate(null).Should().BeNull();
        svc.Validate("").Should().BeNull();
        svc.Validate("not-a-token").Should().BeNull();
        svc.Validate("a.b.c.d").Should().BeNull();
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var svc = MakeService("c2VjcmV0LWE=", lifetimeHours: 1);

        // Forge a token with an expiry in the past, signed with the same key,
        // by issuing through the service then editing the expiry. We can't
        // edit because the signature would mismatch — instead we issue with
        // negative lifetime via reflection or a second service instance.
        var svcExpired = MakeService("c2VjcmV0LWE=", lifetimeHours: -1);
        var tok = svcExpired.IssueToken(Guid.NewGuid());

        // svcExpired produced a token already-expired. svc shares the secret
        // so signature validates, but expiry must be enforced.
        svc.Validate(tok).Should().BeNull();
    }

    private static AgentTokenService MakeService(string secret, int lifetimeHours = 24)
    {
        var opts = Options.Create(new SecurityOptions
        {
            AgentTokenSecret = secret,
            AgentTokenLifetimeHours = lifetimeHours
        });
        return new AgentTokenService(opts, NullLogger<AgentTokenService>.Instance);
    }
}
