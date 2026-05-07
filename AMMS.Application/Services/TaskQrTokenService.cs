using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Productions;
using Microsoft.Extensions.Configuration;

namespace AMMS.Application.Services
{
    public class TaskQrTokenService : ITaskQrTokenService
    {
        private const byte TokenVersion = 1;

        private const int SignatureLength = 10;

        // chỉ gồm số + chữ, không ký tự đặc biệt
        private const string Base62Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        private readonly byte[] _secretBytes;
        private static readonly Dictionary<char, int> _base62Map = Base62Alphabet
            .Select((ch, idx) => new { ch, idx })
            .ToDictionary(x => x.ch, x => x.idx);

        public TaskQrTokenService(IConfiguration config)
        {
            var secret = config["TaskQr:Secret"];
            if (string.IsNullOrWhiteSpace(secret))
                throw new InvalidOperationException("Missing config: TaskQr:Secret");

            _secretBytes = Encoding.UTF8.GetBytes(secret);
        }

        public string CreateToken(int taskId, int qtyGood, TimeSpan ttl)
            => CreateToken(taskId, qtyGood, null, ttl);

        public string CreateToken(int taskId, int qtyGood, IReadOnlyList<TaskMaterialUsageInputDto>? materials, TimeSpan ttl)
        {
            if (taskId <= 0)
                throw new ArgumentException("taskId must be > 0");

            if (qtyGood <= 0)
                throw new ArgumentException("qtyGood must be > 0");

            var expUnix = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds();
            var normalizedMaterials = NormalizeMaterials(materials);

            var body = BuildBody(taskId, qtyGood, expUnix, normalizedMaterials);
            var sig = ComputeSignature(body);

            var raw = new byte[body.Length + sig.Length];
            Buffer.BlockCopy(body, 0, raw, 0, body.Length);
            Buffer.BlockCopy(sig, 0, raw, body.Length, sig.Length);

            return Base62Encode(raw);
        }

        public bool TryValidate(string token, out int taskId, out int qtyGood, out string reason)
        {
            taskId = 0;
            qtyGood = 0;

            if (!TryValidate(token, out TaskQrTokenPayloadDto payload, out reason))
                return false;

            taskId = payload.task_id;
            qtyGood = payload.qty_good;
            return true;
        }

        public bool TryValidate(string token, out TaskQrTokenPayloadDto payload, out string reason)
        {
            payload = new TaskQrTokenPayloadDto();
            reason = "Invalid token";

            if (string.IsNullOrWhiteSpace(token))
            {
                reason = "Token is empty";
                return false;
            }

            // chống ký tự lạ
            for (var i = 0; i < token.Length; i++)
            {
                if (!_base62Map.ContainsKey(token[i]))
                {
                    reason = "Token contains invalid characters";
                    return false;
                }
            }

            byte[] raw;
            try
            {
                raw = Base62Decode(token);
            }
            catch
            {
                reason = "Cannot decode token";
                return false;
            }

            if (raw.Length <= SignatureLength + 1)
            {
                reason = "Token too short";
                return false;
            }

            var bodyLength = raw.Length - SignatureLength;
            var body = new byte[bodyLength];
            var givenSig = new byte[SignatureLength];

            Buffer.BlockCopy(raw, 0, body, 0, bodyLength);
            Buffer.BlockCopy(raw, bodyLength, givenSig, 0, SignatureLength);

            var expectedSig = ComputeSignature(body);
            if (!CryptographicOperations.FixedTimeEquals(givenSig, expectedSig))
            {
                reason = "Token signature is invalid";
                return false;
            }

            try
            {
                payload = ParseBody(body);
            }
            catch
            {
                reason = "Token payload is invalid";
                return false;
            }

            if (payload.exp_unix <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                reason = "QR token has expired";
                return false;
            }

            if (payload.task_id <= 0)
            {
                reason = "task_id is invalid";
                return false;
            }

            if (payload.qty_good <= 0)
            {
                reason = "qty_good is invalid";
                return false;
            }

            payload.materials ??= new List<TaskMaterialUsageInputDto>();
            reason = "";
            return true;
        }

        private byte[] BuildBody(
            int taskId,
            int qtyGood,
            long expUnix,
            List<TaskMaterialUsageInputDto> materials)
        {
            using var ms = new MemoryStream();

            ms.WriteByte(TokenVersion);

            WriteVarUInt(ms, (ulong)taskId);
            WriteVarUInt(ms, (ulong)qtyGood);
            WriteVarUInt(ms, (ulong)expUnix);
            WriteVarUInt(ms, (ulong)materials.Count);

            foreach (var m in materials)
            {
                if (m.material_id <= 0)
                    throw new ArgumentException("material_id must be > 0");

                var usedScaled = ToScaledUInt64(m.quantity_used);
                var leftScaled = ToScaledUInt64(m.quantity_left);

                WriteVarUInt(ms, (ulong)m.material_id);
                WriteVarUInt(ms, usedScaled);
                WriteVarUInt(ms, leftScaled);

                byte flags = 0;
                if (m.is_stock) flags |= 0b0000_0001;
                ms.WriteByte(flags);
            }

            return ms.ToArray();
        }

        private TaskQrTokenPayloadDto ParseBody(byte[] body)
        {
            using var ms = new MemoryStream(body);

            var version = ms.ReadByte();
            if (version != TokenVersion)
                throw new InvalidOperationException($"Unsupported token version: {version}");

            var taskId = (int)ReadVarUInt(ms);
            var qtyGood = (int)ReadVarUInt(ms);
            var expUnix = (long)ReadVarUInt(ms);
            var materialCount = (int)ReadVarUInt(ms);

            var materials = new List<TaskMaterialUsageInputDto>(materialCount);

            for (var i = 0; i < materialCount; i++)
            {
                var materialId = (int)ReadVarUInt(ms);
                var quantityUsed = FromScaledUInt64(ReadVarUInt(ms));
                var quantityLeft = FromScaledUInt64(ReadVarUInt(ms));

                var flag = ms.ReadByte();
                if (flag < 0)
                    throw new EndOfStreamException("Unexpected end of token");

                materials.Add(new TaskMaterialUsageInputDto
                {
                    material_id = materialId,
                    quantity_used = quantityUsed,
                    quantity_left = quantityLeft,
                    is_stock = (flag & 0b0000_0001) != 0
                });
            }

            if (ms.Position != ms.Length)
                throw new InvalidOperationException("Token has unexpected trailing bytes");

            return new TaskQrTokenPayloadDto
            {
                task_id = taskId,
                qty_good = qtyGood,
                exp_unix = expUnix,
                materials = materials
            };
        }

        private byte[] ComputeSignature(byte[] body)
        {
            using var hmac = new HMACSHA256(_secretBytes);
            var full = hmac.ComputeHash(body);

            var shortSig = new byte[SignatureLength];
            Buffer.BlockCopy(full, 0, shortSig, 0, SignatureLength);
            return shortSig;
        }

        private static List<TaskMaterialUsageInputDto> NormalizeMaterials(
            IReadOnlyList<TaskMaterialUsageInputDto>? materials)
        {
            if (materials == null || materials.Count == 0)
                return new List<TaskMaterialUsageInputDto>();

            return materials
                .Select(x => new TaskMaterialUsageInputDto
                {
                    material_id = x.material_id,
                    quantity_used = Math.Round(x.quantity_used, 4),
                    quantity_left = Math.Round(x.quantity_left, 4),
                    is_stock = x.is_stock
                })
                .ToList();
        }

        private static ulong ToScaledUInt64(decimal value)
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Value must be >= 0");

            var scaled = decimal.Round(value * 10000m, 0, MidpointRounding.AwayFromZero);

            if (scaled > ulong.MaxValue)
                throw new OverflowException("Scaled value too large");

            return (ulong)scaled;
        }

        private static decimal FromScaledUInt64(ulong value)
            => value / 10000m;

        private static void WriteVarUInt(Stream stream, ulong value)
        {
            while (value >= 0x80)
            {
                stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            stream.WriteByte((byte)value);
        }

        private static ulong ReadVarUInt(Stream stream)
        {
            ulong result = 0;
            var shift = 0;

            while (true)
            {
                var b = stream.ReadByte();
                if (b < 0)
                    throw new EndOfStreamException("Unexpected end of stream");

                result |= ((ulong)(b & 0x7F)) << shift;

                if ((b & 0x80) == 0)
                    return result;

                shift += 7;
                if (shift > 63)
                    throw new FormatException("VarUInt too large");
            }
        }

        private static string Base62Encode(byte[] data)
        {
            if (data == null || data.Length == 0)
                return "0";

            var value = new BigInteger(data, isUnsigned: true, isBigEndian: true);
            if (value == 0)
                return "0";

            var sb = new StringBuilder();

            while (value > 0)
            {
                value = BigInteger.DivRem(value, 62, out var remainder);
                sb.Insert(0, Base62Alphabet[(int)remainder]);
            }

            return sb.ToString();
        }

        private static byte[] Base62Decode(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new FormatException("Empty token");

            BigInteger value = BigInteger.Zero;

            foreach (var ch in text)
            {
                if (!_base62Map.TryGetValue(ch, out var digit))
                    throw new FormatException($"Invalid Base62 char: {ch}");

                value = (value * 62) + digit;
            }

            return value.ToByteArray(isUnsigned: true, isBigEndian: true);
        }
    }
}