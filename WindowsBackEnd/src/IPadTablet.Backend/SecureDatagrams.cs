using System.Security.Cryptography;
using System.Text;

namespace IPadTablet.Backend;

internal sealed class SecureDatagrams : IDisposable
{
    public const byte VideoEnvelope = 1;
    public const byte ControlEnvelope = 3;
    public const int Overhead = 34;
    private static readonly byte[] Magic = "IPAE"u8.ToArray();
    private static readonly byte[] Salt = "ipad-tablet-secure-udp-v2"u8.ToArray();
    private readonly AesGcm sender;
    private readonly AesGcm receiver;
    private readonly object senderGate = new();
    private readonly object receiverGate = new();
    private readonly HashSet<string> seen = [];
    private readonly Queue<string> seenOrder = [];

    public SecureDatagrams(string token, string sendingDirection, string receivingDirection)
    {
        if (Encoding.UTF8.GetByteCount(token) < 16)
            throw new ArgumentException("Secure UDP token must contain at least 16 UTF-8 bytes.");
        sender = new AesGcm(Derive(token, sendingDirection), 16);
        receiver = new AesGcm(Derive(token, receivingDirection), 16);
    }

    public byte[] Seal(byte type, ReadOnlySpan<byte> plaintext)
    {
        var packet = new byte[18 + plaintext.Length + 16];
        Magic.CopyTo(packet, 0);
        packet[4] = 2;
        packet[5] = type;
        RandomNumberGenerator.Fill(packet.AsSpan(6, 12));
        lock (senderGate)
            sender.Encrypt(packet.AsSpan(6, 12), plaintext,
                packet.AsSpan(18, plaintext.Length), packet.AsSpan(18 + plaintext.Length, 16),
                packet.AsSpan(0, 18));
        return packet;
    }

    public byte[]? Open(byte expectedType, ReadOnlySpan<byte> packet)
    {
        if (packet.Length < Overhead || !packet[..4].SequenceEqual(Magic)
            || packet[4] != 2 || packet[5] != expectedType) return null;
        lock (receiverGate)
        {
            var nonceKey = Convert.ToHexString(packet.Slice(6, 12));
            if (seen.Contains(nonceKey)) return null;
            var plaintext = new byte[packet.Length - Overhead];
            try
            {
                receiver.Decrypt(packet.Slice(6, 12), packet.Slice(18, plaintext.Length),
                    packet[^16..], plaintext, packet[..18]);
            }
            catch (CryptographicException) { return null; }
            seen.Add(nonceKey);
            seenOrder.Enqueue(nonceKey);
            while (seenOrder.Count > 4096) seen.Remove(seenOrder.Dequeue());
            return plaintext;
        }
    }

    private static byte[] Derive(string token, string direction)
    {
        var output = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(token), output,
            Salt, Encoding.UTF8.GetBytes(direction));
        return output;
    }

    public static void AssertCompatibility()
    {
        const string vector =
            "495041450203000102030405060708090a0b505ca39f317c1c0be1bcc641a0f7b5" +
            "9e2f1f31f3372a72a34b9b147297d0";
        using var server = new SecureDatagrams(
            "interoperability-test-token", "server-to-client", "client-to-server");
        var plaintext = server.Open(ControlEnvelope, Convert.FromHexString(vector));
        if (plaintext is null || !plaintext.AsSpan().SequenceEqual("ipad-tablet-v2"u8))
            throw new CryptographicException("Encrypted UDP compatibility self-test failed.");
    }

    public void Dispose() { sender.Dispose(); receiver.Dispose(); }
}
