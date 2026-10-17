using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace ClawBox.Desktop.Services;

public sealed class WirelessException(string code) : InvalidOperationException(MessageFor(code))
{
    public string Code { get; } = code;
    private static string MessageFor(string code) => code switch
    {
        "not_owner" or "already_bound" => "这台盒子已有其他配对，请选择原来的盒子或使用原电脑连接。",
        "identity_mismatch" or "invalid_signature" => "盒子身份与原配对不一致，已停止连接。换板请使用独立的更换板子入口。",
        "wifi_connection_failed" or "wifi_connection_timeout" => "未能连接 Wi-Fi，请核对密码、信号和路由器。原网络配置已保留，可重新选择网络。",
        "wifi_driver_missing" => "盒子未检测到无线网卡，请联系交付人员检查驱动。",
        "invalid_password" => "Wi-Fi 密码格式不正确，请重新输入。",
        "wifi_setup_required" => "未能通过当前网络连接原盒子。如果更换了 Wi-Fi，请通过蓝牙为盒子设置新网络；已有配对会保留。",
        "saved_network_missing" => "盒子中没有找到该网络的已保存配置，请输入密码后连接。",
        "unsupported_security" => "当前网络需要尚未支持的认证方式，请选择个人 Wi-Fi 或手机热点。",
        "bluetooth_unavailable" => "蓝牙不可用，请在电脑设置中开启蓝牙，并将盒子放在附近。",
        "bluetooth_connection" => "蓝牙连接中断或设备未响应，请靠近盒子后重新连接。",
        "session_expired" or "invalid_sequence" => "本次配网连接已失效，请重新连接盒子。软件不会自动重复提交网络配置。",
        "desktop_setup_failed" => "盒子初始化未完成，原配对已保留。请重新连接后重试初始化。",
        "response_timeout" => "盒子尚未确认操作结果。请重新连接并检查当前网络，软件不会自动重发配置。",
        _ => "无线连接未完成，请重新连接盒子后重试。"
    };
}

public sealed class WirelessProtocol : IDisposable
{
    private static readonly byte[] Prefix = Encoding.ASCII.GetBytes("clawbox-network-v1\0");
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly PrivateKeyFile owner;
    private readonly HostAlgorithm algorithm;
    private readonly X25519PrivateKeyParameters ephemeral = new(new SecureRandom());
    private readonly string? expected;
    private byte[] outgoing = [], incoming = [];
    private string session = "";
    private ulong sequence;
    public JsonElement Hello { get; }
    public string Identity { get; private set; } = "";
    public string HostKey { get; private set; } = "";
    public WirelessProtocol(string identityFile, string? expectedIdentity, bool claim)
    {
        owner = new(identityFile);
        algorithm = owner.HostKeyAlgorithms.Single(a => a.Name == "ssh-ed25519");
        expected = expectedIdentity;
        Hello = Element(new
        {
            op = "hello",
            version = 1,
            mode = claim ? "claim" : "resume",
            owner = "ssh-ed25519 " + Convert.ToBase64String(algorithm.Data),
            nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ephemeral = Convert.ToBase64String(ephemeral.GeneratePublicKey().GetEncoded())
        });
    }
    public JsonElement Accept(JsonElement response)
    {
        ThrowError(response);
        JsonElement transcript = response.GetProperty("transcript");
        HostKey = transcript.GetProperty("host_key").GetString()!;
        byte[] blob = Convert.FromBase64String(HostKey.Split(' ')[1]);
        Identity = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        if (Identity != transcript.GetProperty("identity").GetString() || expected is not null && Identity != expected ||
            !Canonical(Hello).SequenceEqual(Canonical(transcript.GetProperty("hello")))) throw new WirelessException("identity_mismatch");
        byte[] data = Canonical(transcript);
        VerifyHost(blob, [.. Prefix, .. data], Convert.FromBase64String(response.GetProperty("signature").GetString()!));
        byte[] secret = new byte[32];
        ephemeral.GenerateSecret(new X25519PublicKeyParameters(Convert.FromBase64String(transcript.GetProperty("ephemeral").GetString()!)), secret, 0);
        byte[] material = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 64, SHA256.HashData(data), Prefix);
        outgoing = material[..32]; incoming = material[32..];
        CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(material);
        session = transcript.GetProperty("session").GetString()!;
        byte[] signature = algorithm.Sign([.. Prefix, .. SHA256.HashData(data)]);
        int offset = 0;
        if (Encoding.ASCII.GetString(ReadBlob(signature, ref offset)) != "ssh-ed25519") throw new WirelessException("invalid_signature");
        byte[] proof = ReadBlob(signature, ref offset);
        if (proof.Length != 64 || offset != signature.Length) throw new WirelessException("invalid_signature");
        return Encrypt(Element(new { op = "authenticate", proof = Convert.ToBase64String(proof) }));
    }
    public JsonElement Encrypt(JsonElement request)
    {
        byte[] plaintext = Canonical(request), cipher = new byte[plaintext.Length], tag = new byte[16];
        using AesGcm aes = new(outgoing, 16);
        aes.Encrypt(Nonce(sequence), plaintext, cipher, tag, Aad("client", sequence));
        CryptographicOperations.ZeroMemory(plaintext);
        return Element(new { session, seq = sequence, ciphertext = Convert.ToBase64String([.. cipher, .. tag]) });
    }
    public JsonElement Decrypt(JsonElement response)
    {
        ThrowError(response);
        ulong used = sequence++;
        if (response.GetProperty("session").GetString() != session || response.GetProperty("seq").GetUInt64() != used)
            throw new WirelessException("invalid_sequence");
        byte[] cipher = Convert.FromBase64String(response.GetProperty("ciphertext").GetString()!);
        if (cipher.Length < 16) throw new WirelessException("invalid_ciphertext");
        byte[] plain = new byte[cipher.Length - 16];
        try
        {
            using AesGcm aes = new(incoming, 16);
            aes.Decrypt(Nonce(used), cipher[..^16], cipher[^16..], plain, Aad("server", used));
            using JsonDocument document = JsonDocument.Parse(plain);
            JsonElement result = document.RootElement.Clone();
            if (!result.GetProperty("ok").GetBoolean()) throw new WirelessException(result.GetProperty("error").GetString()!);
            return result.GetProperty("result").Clone();
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private byte[] Aad(string direction, ulong used) => Canonical(Element(new { session, direction, seq = used }));
    private static byte[] Nonce(ulong used) { byte[] value = new byte[12]; BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(4), used); return value; }
    private static void ThrowError(JsonElement value)
    {
        if (value.TryGetProperty("error", out JsonElement code) && !value.TryGetProperty("ciphertext", out _)) throw new WirelessException(code.GetString()!);
    }
    private static void VerifyHost(byte[] blob, byte[] data, byte[] signature)
    {
        int offset = 0;
        if (Encoding.ASCII.GetString(ReadBlob(blob, ref offset)) != "ssh-ed25519") throw new WirelessException("identity_mismatch");
        byte[] key = ReadBlob(blob, ref offset);
        if (key.Length != 32 || offset != blob.Length) throw new WirelessException("identity_mismatch");
        Ed25519Signer verifier = new(); verifier.Init(false, new Ed25519PublicKeyParameters(key));
        verifier.BlockUpdate(data, 0, data.Length);
        if (!verifier.VerifySignature(signature)) throw new WirelessException("invalid_signature");
    }
    internal static byte[] ReadBlob(byte[] value, ref int offset)
    {
        if (offset < 0 || offset > value.Length - 4) throw new WirelessException("invalid_message");
        uint length = BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(offset)); offset += 4;
        if (length > value.Length - offset) throw new WirelessException("invalid_message");
        byte[] result = value.AsSpan(offset, (int)length).ToArray(); offset += (int)length; return result;
    }
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    public static byte[] Canonical(JsonElement value)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) Write(writer, value);
        StringBuilder result = new();
        foreach (char character in Encoding.UTF8.GetString(stream.ToArray()))
            if (character > 127) result.Append("\\u").Append(((int)character).ToString("x4")); else result.Append(character);
        return Encoding.ASCII.GetBytes(result.ToString());
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array) { writer.WriteStartArray(); foreach (JsonElement item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
    public void Dispose() { owner.Dispose(); CryptographicOperations.ZeroMemory(incoming); CryptographicOperations.ZeroMemory(outgoing); }
}
