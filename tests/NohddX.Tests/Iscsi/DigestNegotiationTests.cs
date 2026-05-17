using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NohddX.Core.Configuration;
using NohddX.Core.Interfaces;
using NohddX.Iscsi;
using NohddX.Iscsi.Handlers;
using NohddX.Iscsi.Protocol;
using NohddX.Iscsi.Session;
using Xunit;

/// <summary>
/// Login-stage HeaderDigest / DataDigest negotiation per RFC 3720 §12.1.
/// The target's job is to pick exactly one algorithm from the initiator's
/// comma-separated offer; we prefer CRC32C for the integrity it provides.
/// These tests pin both the choice and the side-effect (session flags
/// that gate read/write framing).
/// </summary>
namespace NohddX.Tests.Iscsi;

public class DigestNegotiationTests
{
    [Theory]
    [InlineData("CRC32C,None",  "CRC32C", true)]   // both offered, we pick CRC32C
    [InlineData("None,CRC32C",  "CRC32C", true)]   // order doesn't matter
    [InlineData("CRC32C",       "CRC32C", true)]   // initiator demands CRC32C only
    [InlineData("None",         "None",   false)]  // initiator can't do CRC32C
    [InlineData("",             "None",   false)]  // empty value -> default None
    public async Task HeaderDigest_negotiation_prefers_CRC32C_when_offered(
        string offered, string expectedNegotiated, bool expectedFlag)
    {
        var (service, session) = Build();

        var loginText = $"InitiatorName=iqn.test:i\0HeaderDigest={offered}\0";
        var pdu = MakeLoginPdu(loginText, csg: 0, nsg: 0, transit: false);
        var responses = await InvokeHandleLoginAsync(service, pdu, session);

        var responseParams = IscsiPdu.ParseTextData(responses[0].DataSegment);
        responseParams["HeaderDigest"].Should().Be(expectedNegotiated);
        session.HeaderDigestEnabled.Should().Be(expectedFlag);
    }

    [Theory]
    [InlineData("CRC32C,None", "CRC32C", true)]
    [InlineData("None",        "None",   false)]
    public async Task DataDigest_negotiation_same_policy(string offered, string expected, bool expectedFlag)
    {
        var (service, session) = Build();

        var loginText = $"InitiatorName=iqn.test:i\0DataDigest={offered}\0";
        var pdu = MakeLoginPdu(loginText, csg: 0, nsg: 0, transit: false);
        var responses = await InvokeHandleLoginAsync(service, pdu, session);

        var responseParams = IscsiPdu.ParseTextData(responses[0].DataSegment);
        responseParams["DataDigest"].Should().Be(expected);
        session.DataDigestEnabled.Should().Be(expectedFlag);
    }

    [Fact]
    public async Task Default_when_initiator_omits_keys_is_None()
    {
        // No HeaderDigest / DataDigest at all -> we MUST default both to None,
        // otherwise we'd be silently enforcing digests on initiators that
        // don't know they need to send them.
        var (service, session) = Build();

        var loginText = "InitiatorName=iqn.test:i\0";
        var pdu = MakeLoginPdu(loginText, csg: 0, nsg: 0, transit: false);
        var responses = await InvokeHandleLoginAsync(service, pdu, session);

        var responseParams = IscsiPdu.ParseTextData(responses[0].DataSegment);
        responseParams["HeaderDigest"].Should().Be("None");
        responseParams["DataDigest"].Should().Be("None");
        session.HeaderDigestEnabled.Should().BeFalse();
        session.DataDigestEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_digest_algorithm_falls_back_to_None()
    {
        // If a future initiator offers e.g. "SHA1,None" we must not crash
        // and must not silently pick something we can't compute; pick None.
        var (service, session) = Build();

        var loginText = "HeaderDigest=SHA1,Blake3\0";
        var pdu = MakeLoginPdu(loginText, csg: 0, nsg: 0, transit: false);
        var responses = await InvokeHandleLoginAsync(service, pdu, session);

        var responseParams = IscsiPdu.ParseTextData(responses[0].DataSegment);
        responseParams["HeaderDigest"].Should().Be("None",
            "we only support CRC32C; unsupported algorithms must degrade to None");
        session.HeaderDigestEnabled.Should().BeFalse();
    }

    // ── Test infrastructure ────────────────────────────────────────────

    private static (IscsiTargetService service, IscsiSession session) Build()
    {
        var options = Options.Create(new NohddxOptions
        {
            Iscsi = new IscsiOptions { ChapEnabled = false }
        });

        var cow = new Moq.Mock<ICowStorageEngine>().Object;
        var sessionMgr = new IscsiSessionManager();
        var scsiHandler = new ScsiCommandHandler(NullLogger<ScsiCommandHandler>.Instance);
        var registry = new TargetRegistry();
        var service = new IscsiTargetService(cow, sessionMgr, scsiHandler, registry, options,
            NullLogger<IscsiTargetService>.Instance);

        var session = new IscsiSession(Guid.NewGuid().ToString());
        return (service, session);
    }

    private static IscsiPdu MakeLoginPdu(string text, byte csg, byte nsg, bool transit)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var header = new byte[IscsiConstants.HeaderSize];
        header[0] = IscsiConstants.OpcodeLoginRequest;
        if (transit) header[1] |= 0x80;
        header[1] |= (byte)((csg & 0x03) << 2);
        header[1] |= (byte)(nsg & 0x03);
        uint dl = (uint)data.Length;
        header[5] = (byte)((dl >> 16) & 0xFF);
        header[6] = (byte)((dl >> 8) & 0xFF);
        header[7] = (byte)(dl & 0xFF);
        return IscsiPdu.Parse(header, data);
    }

    private static async Task<List<IscsiPdu>> InvokeHandleLoginAsync(
        IscsiTargetService service, IscsiPdu pdu, IscsiSession session)
    {
        var method = typeof(IscsiTargetService).GetMethod("HandleLoginAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method.Should().NotBeNull();
        var task = (Task<List<IscsiPdu>>)method!.Invoke(service, new object[] { pdu, session, CancellationToken.None })!;
        return await task;
    }
}
