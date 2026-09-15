using Rumble.Net;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.Samples;

[Sample("Security", "Certificates and TLS pinning", "Generate a client identity, trust a server on first use and pin its fingerprint.",
    Description = "Mumble identifies registered users by their client certificate. Server certificates are usually self-signed: record the fingerprint on first connect (ServerCertificateReceived) and pin it afterwards to detect impostors.",
    Order = 100)]
public static class SecuritySample
{
    public static async Task RunAsync(SampleContext ctx)
    {
        var identity = RumbleNative.GenerateCertificate("GalleryPlayer");
        ctx.Log($"Generated client certificate, SHA-1 {identity.Sha1Fingerprint}");

        // 1) Trust on first use: accept and remember the fingerprint.
        var options = ctx.CreateOptions("GalleryPlayer");
        options.CertificatePem = identity.CertificatePem;
        options.PrivateKeyPem = identity.PrivateKeyPem;
        string? serverFingerprint = null;
        await using (var client = new RumbleClient(options))
        {
            client.ServerCertificateReceived += (_, cert) => serverFingerprint = cert.Sha1Fingerprint;
            await client.ConnectAsync(ctx.Token);
            ctx.Success($"First connection accepted server certificate {serverFingerprint}");
        }

        // 2) Pin the remembered fingerprint.
        options.TlsVerification = TlsVerification.Pinned;
        options.PinnedFingerprint = serverFingerprint;
        await using (var pinned = new RumbleClient(options))
        {
            await pinned.ConnectAsync(ctx.Token);
            ctx.Success("Pinned connection verified the same server");
        }

        // 3) A wrong pin is rejected before any credentials are sent.
        options.PinnedFingerprint = new string('0', 40);
        await using var impostorCheck = new RumbleClient(options);
        try
        {
            await impostorCheck.ConnectAsync(ctx.Token);
            ctx.Warn("Unexpected: connected with a wrong pin");
        }
        catch (RumbleConnectionException ex)
        {
            ctx.Success($"Wrong pin rejected: {ex.Message}");
        }
    }
}
