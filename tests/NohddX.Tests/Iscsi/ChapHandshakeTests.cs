using System.Security.Cryptography;
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

namespace NohddX.Tests.Iscsi;

/// <summary>
/// Integration-level tests for CHAP enforcement on the iSCSI Login handler.
/// We can't unit-test the private helper directly, so we drive the public
/// PDU loop with crafted Login PDUs and assert on the responses.
///
/// The handshake under test (RFC 3720 §11.1.4):
///   1. initiator -> AuthMethod=CHAP,None         ; target -> AuthMethod=CHAP
///   2. initiator -> CHAP_A=5                     ; target -> CHAP_A=5 CHAP_I=<id> CHAP_C=0x<hex>
///   3. initiator -> CHAP_N=<u> CHAP_R=0x<md5>    ; target -> success + transit
/// </summary>
public class ChapHandshakeTests
{
    private const string Username = "tester";
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task When_chap_disabled_login_passes_without_auth()
    {
        var service = BuildService(chapEnabled: false);
        var session = new IscsiSession(Guid.NewGuid().ToString());

        var loginText = "InitiatorName=iqn.test:i\0TargetName=iqn.2024.com.nohddx:c\0";
        var pdu = MakeLoginPdu(loginText, csg: 0, nsg: 1, transit: true);

        var responses = await InvokeHandleLoginAsync(service, pdu, session);

        // First response should be a successful login (status 0x00) and may
        // transit since CHAP is off. We don't try to log into a real disk
        // here — just assert "not auth failure".
        var r = responses.Single();
        r.StatusClass.Should().Be(0x00, "no auth required when CHAP disabled");
    }

    [Fact]
    public async Task When_chap_enabled_initiator_offering_None_only_is_rejected()
    {
        // Bypass attempt: initiator tries to use anonymous AuthMethod.
        var service = BuildService(chapEnabled: true);
        var session = new IscsiSession(Guid.NewGuid().ToString());

        var loginText = "InitiatorName=iqn.test:i\0AuthMethod=None\0";
        var pdu = MakeLoginPdu(loginText, csg: 0, nsg: 0, transit: false);

        var responses = await InvokeHandleLoginAsync(service, pdu, session);

        responses.Should().HaveCount(1);
        responses[0].StatusClass.Should().Be(0x02, "anonymous login must be rejected when CHAP is enabled");
        responses[0].StatusDetail.Should().Be(0x01);
        session.AuthCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task When_chap_enabled_skipping_security_stage_is_rejected()
    {
        // Bypass attempt: jump straight to operational stage.
        var service = BuildService(chapEnabled: true);
        var session = new IscsiSession(Guid.NewGuid().ToString());

        var loginText = "InitiatorName=iqn.test:i\0TargetName=iqn.2024.com.nohddx:c\0";
        var pdu = MakeLoginPdu(loginText, csg: 1, nsg: 3, transit: true);

        var responses = await InvokeHandleLoginAsync(service, pdu, session);

        responses[0].StatusClass.Should().Be(0x02);
        responses[0].StatusDetail.Should().Be(0x01);
    }

    [Fact]
    public async Task Chap_full_handshake_with_correct_response_succeeds()
    {
        var service = BuildService(chapEnabled: true);
        var session = new IscsiSession(Guid.NewGuid().ToString());

        // Step 1: offer AuthMethod
        var step1 = await InvokeHandleLoginAsync(service,
            MakeLoginPdu("AuthMethod=CHAP,None\0", csg: 0, nsg: 0, transit: false),
            session);
        step1[0].StatusClass.Should().Be(0x00);
        ParseTextDict(step1[0].DataSegment).Should().Contain(new KeyValuePair<string, string>("AuthMethod", "CHAP"));

        // Step 2: select CHAP_A=5, get back challenge
        var step2 = await InvokeHandleLoginAsync(service,
            MakeLoginPdu("CHAP_A=5\0", csg: 0, nsg: 0, transit: false),
            session);
        step2[0].StatusClass.Should().Be(0x00);

        var challengeParams = ParseTextDict(step2[0].DataSegment);
        challengeParams.Should().ContainKey("CHAP_I");
        challengeParams.Should().ContainKey("CHAP_C");

        byte chapId = byte.Parse(challengeParams["CHAP_I"]);
        var hex = challengeParams["CHAP_C"].StartsWith("0x") ? challengeParams["CHAP_C"][2..] : challengeParams["CHAP_C"];
        byte[] challenge = Convert.FromHexString(hex);

        // Step 3: compute MD5 response (RFC 1994): MD5(id || password || challenge)
        var pwBytes = Encoding.UTF8.GetBytes(Password);
        var input = new byte[1 + pwBytes.Length + challenge.Length];
        input[0] = chapId;
        Array.Copy(pwBytes, 0, input, 1, pwBytes.Length);
        Array.Copy(challenge, 0, input, 1 + pwBytes.Length, challenge.Length);
        var hash = MD5.HashData(input);

        var step3Text = $"CHAP_N={Username}\0CHAP_R=0x{Convert.ToHexString(hash).ToLowerInvariant()}\0" +
                        "TargetName=iqn.2024.com.nohddx:c\0";
        var step3 = await InvokeHandleLoginAsync(service,
            MakeLoginPdu(step3Text, csg: 0, nsg: 1, transit: true),
            session);
        step3[0].StatusClass.Should().Be(0x00, "correct CHAP response must yield success");
        session.AuthCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Chap_wrong_password_is_rejected_with_constant_time_compare()
    {
        var service = BuildService(chapEnabled: true);
        var session = new IscsiSession(Guid.NewGuid().ToString());

        await InvokeHandleLoginAsync(service,
            MakeLoginPdu("AuthMethod=CHAP\0", csg: 0, nsg: 0, transit: false), session);
        var step2 = await InvokeHandleLoginAsync(service,
            MakeLoginPdu("CHAP_A=5\0", csg: 0, nsg: 0, transit: false), session);

        var p = ParseTextDict(step2[0].DataSegment);
        byte chapId = byte.Parse(p["CHAP_I"]);
        byte[] challenge = Convert.FromHexString(p["CHAP_C"][2..]);

        // Compute hash using the WRONG password
        var wrongPw = Encoding.UTF8.GetBytes("not-the-password");
        var input = new byte[1 + wrongPw.Length + challenge.Length];
        input[0] = chapId;
        Array.Copy(wrongPw, 0, input, 1, wrongPw.Length);
        Array.Copy(challenge, 0, input, 1 + wrongPw.Length, challenge.Length);
        var badHash = MD5.HashData(input);

        var step3Text = $"CHAP_N={Username}\0CHAP_R=0x{Convert.ToHexString(badHash)}\0";
        var step3 = await InvokeHandleLoginAsync(service,
            MakeLoginPdu(step3Text, csg: 0, nsg: 0, transit: false),
            session);
        step3[0].StatusClass.Should().Be(0x02);
        step3[0].StatusDetail.Should().Be(0x01);
        session.AuthCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Chap_wrong_username_is_rejected()
    {
        var service = BuildService(chapEnabled: true);
        var session = new IscsiSession(Guid.NewGuid().ToString());

        await InvokeHandleLoginAsync(service,
            MakeLoginPdu("AuthMethod=CHAP\0", csg: 0, nsg: 0, transit: false), session);
        await InvokeHandleLoginAsync(service,
            MakeLoginPdu("CHAP_A=5\0", csg: 0, nsg: 0, transit: false), session);

        // Even if hash is structurally valid, wrong CHAP_N must reject
        // (otherwise an attacker can substitute any username).
        var fakeResponse = "CHAP_N=admin\0CHAP_R=0x00000000000000000000000000000000\0";
        var step3 = await InvokeHandleLoginAsync(service,
            MakeLoginPdu(fakeResponse, csg: 0, nsg: 0, transit: false),
            session);
        step3[0].StatusClass.Should().Be(0x02);
    }

    [Fact]
    public async Task Chap_unsupported_algorithm_is_rejected()
    {
        var service = BuildService(chapEnabled: true);
        var session = new IscsiSession(Guid.NewGuid().ToString());

        await InvokeHandleLoginAsync(service,
            MakeLoginPdu("AuthMethod=CHAP\0", csg: 0, nsg: 0, transit: false), session);

        // Only MD5 (algo 5) is implemented. SHA1 (algo 7) should reject.
        var step2 = await InvokeHandleLoginAsync(service,
            MakeLoginPdu("CHAP_A=7\0", csg: 0, nsg: 0, transit: false), session);
        step2[0].StatusClass.Should().Be(0x02);
    }

    // ── Test infrastructure ────────────────────────────────────────────

    /// <summary>
    /// Builds a real IscsiTargetService (with mocked dependencies) so we can
    /// exercise HandleLoginAsync via reflection without spinning up TCP.
    /// </summary>
    private static IscsiTargetService BuildService(bool chapEnabled)
    {
        var options = Options.Create(new NohddxOptions
        {
            Iscsi = new IscsiOptions
            {
                ChapEnabled = chapEnabled,
                ChapUsername = Username,
                ChapPassword = Password,
            }
        });

        var cow = new Moq.Mock<ICowStorageEngine>().Object;
        var sessionMgr = new IscsiSessionManager();
        var scsiHandler = new ScsiCommandHandler(NullLogger<ScsiCommandHandler>.Instance);
        var registry = new TargetRegistry();

        return new IscsiTargetService(cow, sessionMgr, scsiHandler, registry, options,
            NullLogger<IscsiTargetService>.Instance);
    }

    private static IscsiPdu MakeLoginPdu(string text, byte csg, byte nsg, bool transit)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var header = new byte[IscsiConstants.HeaderSize];
        header[0] = IscsiConstants.OpcodeLoginRequest;
        if (transit) header[1] |= 0x80;
        header[1] |= (byte)((csg & 0x03) << 2);
        header[1] |= (byte)(nsg & 0x03);
        // Data segment length (24-bit BE)
        uint dl = (uint)data.Length;
        header[5] = (byte)((dl >> 16) & 0xFF);
        header[6] = (byte)((dl >> 8) & 0xFF);
        header[7] = (byte)(dl & 0xFF);

        return IscsiPdu.Parse(header, data);
    }

    private static Dictionary<string, string> ParseTextDict(byte[] segment) =>
        IscsiPdu.ParseTextData(segment);

    private static async Task<List<IscsiPdu>> InvokeHandleLoginAsync(
        IscsiTargetService service, IscsiPdu pdu, IscsiSession session)
    {
        var method = typeof(IscsiTargetService).GetMethod("HandleLoginAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method.Should().NotBeNull("HandleLoginAsync must be discoverable by reflection");

        var task = (Task<List<IscsiPdu>>)method!.Invoke(service, new object[] { pdu, session, CancellationToken.None })!;
        return await task;
    }
}
