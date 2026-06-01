using System.IO.Compression;
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
        private const byte TokenVersion = 3;
        private const int SignatureLength = 8;
        private const byte EnvelopeFlagCompressed = 0b0000_0001;

        private const byte PayloadFlagManualInput = 0b0000_0001;
        private const byte PayloadFlagHasReason = 0b0000_0010;
        private const byte PayloadFlagHasReportImageUrl = 0b0000_0100;
        private const byte PayloadFlagHasMaterials = 0b0000_1000;
        private const byte PayloadFlagHasReferenceInputs = 0b0001_0000;
        private const byte PayloadFlagHasOutputs = 0b0010_0000;
        private const string Base32Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        private const byte LegacyTokenVersionV2 = 2;
        private const int LegacySignatureLength = 10;
        private const string LegacyBase62Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        private const string Base36Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        private readonly byte[] _secretBytes;
        private static readonly Dictionary<char, int> _base32Map = BuildBase32Map();
        private static readonly Dictionary<char, int> _legacyBase62Map = LegacyBase62Alphabet
            .Select((ch, idx) => new { ch, idx })
            .ToDictionary(x => x.ch, x => x.idx);

        public TaskQrTokenService(IConfiguration config)
        {
            var secret = config["TaskQr:Secret"];

            if (string.IsNullOrWhiteSpace(secret))
                throw new InvalidOperationException("Missing config: TaskQr:Secret");

            _secretBytes = Encoding.UTF8.GetBytes(secret);
        }

        public string CreateToken(
    int taskId,
    int qtyGood,
    IReadOnlyList<TaskMaterialUsageInputDto>? materials,
    TimeSpan ttl,
    bool useManualInput,
    string? reason,
    string? reportImageUrl,
    IReadOnlyList<TaskReferenceUsageInputDto>? referenceInputs,
    IReadOnlyList<TaskOutputReportDto>? outputs)
        {
            if (taskId <= 0)
                throw new ArgumentException("taskId must be > 0");

            if (qtyGood <= 0)
                throw new ArgumentException("qtyGood must be > 0");

            if (ttl <= TimeSpan.Zero)
                throw new ArgumentException("ttl must be > 0");

            var normalizedMaterials = NormalizeMaterials(materials);
            var normalizedRefs = NormalizeReferenceInputs(referenceInputs);
            var normalizedOutputs = NormalizeOutputs(outputs);

            var expUnixMinutes = DateTimeOffset.UtcNow
                .Add(ttl)
                .ToUnixTimeSeconds() / 60;

            var payloadBody = BuildPayloadV3(
                taskId: taskId,
                qtyGood: qtyGood,
                expUnixMinutes: expUnixMinutes,
                materials: normalizedMaterials,
                useManualInput: useManualInput,
                reason: NormalizeTokenText(reason, 1000),
                reportImageUrl: NormalizeTokenText(reportImageUrl, 8000),
                referenceInputs: normalizedRefs,
                outputs: normalizedOutputs);

            var envelopeFlags = (byte)0;
            var envelopeContent = payloadBody;

            if (payloadBody.Length >= 48)
            {
                var compressed = BrotliCompress(payloadBody);

                if (compressed.Length + 2 < payloadBody.Length)
                {
                    envelopeFlags |= EnvelopeFlagCompressed;
                    envelopeContent = compressed;
                }
            }

            var envelope = new byte[2 + envelopeContent.Length];
            envelope[0] = TokenVersion;
            envelope[1] = envelopeFlags;

            Buffer.BlockCopy(
                envelopeContent,
                0,
                envelope,
                2,
                envelopeContent.Length);

            var sig = ComputeSignature(envelope, SignatureLength);

            var raw = new byte[envelope.Length + sig.Length];

            Buffer.BlockCopy(envelope, 0, raw, 0, envelope.Length);
            Buffer.BlockCopy(sig, 0, raw, envelope.Length, sig.Length);

            /*
             * FIX:
             * Trước đây dùng Base32 nên token dài hơn.
             * Bây giờ dùng Base36: chỉ A-Z + 0-9, không ký tự đặc biệt.
             */
            return Base36Encode(raw);
        }

        public bool TryValidate(
            string token,
            out int taskId,
            out int qtyGood,
            out string reason)
        {
            taskId = 0;
            qtyGood = 0;

            if (!TryValidate(token, out TaskQrTokenPayloadDto payload, out reason))
                return false;

            taskId = payload.task_id;
            qtyGood = payload.qty_good;

            return true;
        }

        public bool TryValidate(
    string token,
    out TaskQrTokenPayloadDto payload,
    out string reason)
        {
            payload = new TaskQrTokenPayloadDto();
            reason = "";

            if (string.IsNullOrWhiteSpace(token))
            {
                reason = "Token is empty";
                return false;
            }

            byte[] raw;

            try
            {
                raw = Base36Decode(token);
            }
            catch (Exception ex)
            {
                reason = $"Không decode được Base36 token. {ex.Message}";
                return false;
            }

            return TryValidateRawBytes(
                raw,
                out payload,
                out reason);
        }

        private bool TryValidateRawBytes(
    byte[] raw,
    out TaskQrTokenPayloadDto payload,
    out string reason)
        {
            payload = new TaskQrTokenPayloadDto();
            reason = "";

            try
            {
                if (raw == null || raw.Length < 2 + SignatureLength)
                {
                    reason = "Token quá ngắn.";
                    return false;
                }

                var envelopeLength = raw.Length - SignatureLength;

                var envelope = raw
                    .Take(envelopeLength)
                    .ToArray();

                var sig = raw
                    .Skip(envelopeLength)
                    .Take(SignatureLength)
                    .ToArray();

                var expectedSig = ComputeSignature(
                    envelope,
                    SignatureLength);

                if (!CryptographicOperations.FixedTimeEquals(sig, expectedSig))
                {
                    reason = "Token signature không hợp lệ.";
                    return false;
                }

                var version = envelope[0];

                if (version != TokenVersion)
                {
                    reason = $"Token version không hỗ trợ: {version}.";
                    return false;
                }

                var envelopeFlags = envelope[1];

                var payloadBytes = envelope
                    .Skip(2)
                    .ToArray();

                if ((envelopeFlags & EnvelopeFlagCompressed) != 0)
                {
                    payloadBytes = BrotliDecompress(payloadBytes);
                }

                payload = ParsePayloadV3(payloadBytes);

                var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                if (payload.exp_unix <= nowUnix)
                {
                    reason = "Token đã hết hạn.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        private byte[] BuildPayloadV3(
    int taskId,
    int qtyGood,
    long expUnixMinutes,
    List<TaskMaterialUsageInputDto> materials,
    bool useManualInput,
    string? reason,
    string? reportImageUrl,
    List<TaskReferenceUsageInputDto> referenceInputs,
    List<TaskOutputReportDto> outputs)
        {
            using var ms = new MemoryStream();

            WriteVarUInt(ms, (ulong)taskId);
            WriteVarUInt(ms, (ulong)qtyGood);
            WriteVarUInt(ms, (ulong)expUnixMinutes);

            byte flags = 0;

            if (useManualInput)
                flags |= PayloadFlagManualInput;

            if (!string.IsNullOrWhiteSpace(reason))
                flags |= PayloadFlagHasReason;

            if (!string.IsNullOrWhiteSpace(reportImageUrl))
                flags |= PayloadFlagHasReportImageUrl;

            if (materials.Count > 0)
                flags |= PayloadFlagHasMaterials;

            if (referenceInputs.Count > 0)
                flags |= PayloadFlagHasReferenceInputs;

            if (outputs.Count > 0)
                flags |= PayloadFlagHasOutputs;

            ms.WriteByte(flags);

            if ((flags & PayloadFlagHasReason) != 0)
                WriteNullableString(ms, reason);

            if ((flags & PayloadFlagHasReportImageUrl) != 0)
                WriteNullableString(ms, reportImageUrl);

            if ((flags & PayloadFlagHasMaterials) != 0)
            {
                WriteVarUInt(ms, (ulong)materials.Count);

                foreach (var m in materials)
                {
                    if (m.material_id <= 0)
                        throw new ArgumentException("material_id must be > 0");

                    WriteVarUInt(ms, (ulong)m.material_id);
                    WriteVarUInt(ms, ToScaledUInt64(m.quantity_used));
                    WriteVarUInt(ms, ToScaledUInt64(m.quantity_left));

                    byte materialFlags = 0;

                    if (m.is_stock)
                        materialFlags |= 0b0000_0001;

                    ms.WriteByte(materialFlags);
                }
            }

            if ((flags & PayloadFlagHasReferenceInputs) != 0)
            {
                WriteVarUInt(ms, (ulong)referenceInputs.Count);

                foreach (var input in referenceInputs)
                {
                    WriteNullableString(ms, input.input_code);
                    WriteNullableString(ms, input.input_name);
                    WriteNullableString(ms, input.unit);
                    WriteVarUInt(ms, ToScaledUInt64(input.quantity_used));
                    WriteVarUInt(ms, ToScaledUInt64(input.quantity_left));
                }
            }

            if ((flags & PayloadFlagHasOutputs) != 0)
            {
                WriteVarUInt(ms, (ulong)outputs.Count);

                foreach (var output in outputs)
                {
                    WriteNullableString(ms, output.output_code);
                    WriteNullableString(ms, output.output_name);
                    WriteNullableString(ms, output.unit);
                    WriteVarUInt(ms, ToScaledUInt64(output.quantity_good));
                    WriteVarUInt(ms, ToScaledUInt64(output.quantity_bad));
                }
            }

            return ms.ToArray();
        }

        private TaskQrTokenPayloadDto ParsePayloadV3(byte[] body)
        {
            using var ms = new MemoryStream(body);

            var taskId = (int)ReadVarUInt(ms);
            var qtyGood = (int)ReadVarUInt(ms);
            var expUnixMinutes = (long)ReadVarUInt(ms);

            var flags = ms.ReadByte();

            if (flags < 0)
                throw new EndOfStreamException("Unexpected end of token");

            var reason = (flags & PayloadFlagHasReason) != 0
                ? ReadNullableString(ms)
                : null;

            var reportImageUrl = (flags & PayloadFlagHasReportImageUrl) != 0
                ? ReadNullableString(ms)
                : null;

            var materials = new List<TaskMaterialUsageInputDto>();

            if ((flags & PayloadFlagHasMaterials) != 0)
            {
                var materialCount = (int)ReadVarUInt(ms);

                for (var i = 0; i < materialCount; i++)
                {
                    var materialId = (int)ReadVarUInt(ms);
                    var quantityUsed = FromScaledUInt64(ReadVarUInt(ms));
                    var quantityLeft = FromScaledUInt64(ReadVarUInt(ms));

                    var materialFlags = ms.ReadByte();

                    if (materialFlags < 0)
                        throw new EndOfStreamException("Unexpected end of token");

                    materials.Add(new TaskMaterialUsageInputDto
                    {
                        material_id = materialId,
                        quantity_used = quantityUsed,
                        quantity_left = quantityLeft,
                        is_stock = (materialFlags & 0b0000_0001) != 0
                    });
                }
            }

            var referenceInputs = new List<TaskReferenceUsageInputDto>();

            if ((flags & PayloadFlagHasReferenceInputs) != 0)
            {
                var referenceInputCount = (int)ReadVarUInt(ms);

                for (var i = 0; i < referenceInputCount; i++)
                {
                    referenceInputs.Add(new TaskReferenceUsageInputDto
                    {
                        input_code = ReadNullableString(ms) ?? "",
                        input_name = ReadNullableString(ms),
                        unit = ReadNullableString(ms),
                        quantity_used = FromScaledUInt64(ReadVarUInt(ms)),
                        quantity_left = FromScaledUInt64(ReadVarUInt(ms))
                    });
                }
            }

            var outputs = new List<TaskOutputReportDto>();

            if ((flags & PayloadFlagHasOutputs) != 0)
            {
                var outputCount = (int)ReadVarUInt(ms);

                for (var i = 0; i < outputCount; i++)
                {
                    outputs.Add(new TaskOutputReportDto
                    {
                        output_code = ReadNullableString(ms) ?? "",
                        output_name = ReadNullableString(ms),
                        unit = ReadNullableString(ms),
                        quantity_good = FromScaledUInt64(ReadVarUInt(ms)),
                        quantity_bad = FromScaledUInt64(ReadVarUInt(ms))
                    });
                }
            }

            if (ms.Position != ms.Length)
                throw new InvalidOperationException("Token has unexpected trailing bytes");

            return new TaskQrTokenPayloadDto
            {
                task_id = taskId,
                qty_good = qtyGood,
                exp_unix = expUnixMinutes * 60,
                use_manual_input = (flags & PayloadFlagManualInput) != 0,
                reason = reason,
                report_image_url = reportImageUrl,
                materials = materials,
                reference_inputs = referenceInputs,
                outputs = outputs
            };
        }

        private static string Base36Encode(byte[] data)
        {
            if (data == null || data.Length == 0)
                return "";

            var leadingZeroCount = data.TakeWhile(x => x == 0).Count();

            var value = new BigInteger(
                data,
                isUnsigned: true,
                isBigEndian: true);

            if (value.IsZero)
                return "0";

            var chars = new List<char>();

            while (value > 0)
            {
                value = BigInteger.DivRem(
                    value,
                    36,
                    out var remainder);

                chars.Add(Base36Alphabet[(int)remainder]);
            }

            chars.Reverse();

            var result = new string(chars.ToArray());

            if (leadingZeroCount > 0)
                result = new string('0', leadingZeroCount) + result;

            return result;
        }

        private static byte[] Base36Decode(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return Array.Empty<byte>();

            var clean = input
                .Trim()
                .Replace(" ", "")
                .Replace("-", "")
                .ToUpperInvariant();

            var leadingZeroCount = clean.TakeWhile(x => x == '0').Count();

            var value = BigInteger.Zero;

            foreach (var ch in clean)
            {
                var digit = Base36Alphabet.IndexOf(ch);

                if (digit < 0)
                    throw new InvalidOperationException($"Token chứa ký tự không hợp lệ: {ch}");

                value *= 36;
                value += digit;
            }

            var bytes = value.IsZero
                ? Array.Empty<byte>()
                : value.ToByteArray(
                    isUnsigned: true,
                    isBigEndian: true);

            if (leadingZeroCount <= 0)
                return bytes;

            return Enumerable
                .Repeat((byte)0, leadingZeroCount)
                .Concat(bytes)
                .ToArray();
        }

        private TaskQrTokenPayloadDto ParseLegacyBodyV1(MemoryStream ms)
        {
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
                use_manual_input = false,
                reason = null,
                report_image_url = null,
                materials = materials,
                reference_inputs = new List<TaskReferenceUsageInputDto>(),
                outputs = new List<TaskOutputReportDto>()
            };
        }

        private byte[] ComputeSignature(byte[] body, int signatureLength)
        {
            using var hmac = new HMACSHA256(_secretBytes);
            var full = hmac.ComputeHash(body);

            var shortSig = new byte[signatureLength];

            Buffer.BlockCopy(full, 0, shortSig, 0, signatureLength);

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

        private static List<TaskReferenceUsageInputDto> NormalizeReferenceInputs(
            IReadOnlyList<TaskReferenceUsageInputDto>? inputs)
        {
            return (inputs ?? Array.Empty<TaskReferenceUsageInputDto>())
                .Where(x => !string.IsNullOrWhiteSpace(x.input_code))
                .Select(x => new TaskReferenceUsageInputDto
                {
                    input_code = x.input_code.Trim(),
                    input_name = NormalizeTokenText(x.input_name, 300),
                    unit = NormalizeTokenText(x.unit, 50),
                    quantity_used = Math.Round(x.quantity_used, 4),
                    quantity_left = Math.Round(x.quantity_left, 4)
                })
                .ToList();
        }

        private static List<TaskOutputReportDto> NormalizeOutputs(
            IReadOnlyList<TaskOutputReportDto>? outputs)
        {
            return (outputs ?? Array.Empty<TaskOutputReportDto>())
                .Where(x => !string.IsNullOrWhiteSpace(x.output_code))
                .Select(x => new TaskOutputReportDto
                {
                    output_code = x.output_code.Trim(),
                    output_name = NormalizeTokenText(x.output_name, 300),
                    unit = NormalizeTokenText(x.unit, 50),
                    quantity_good = Math.Round(x.quantity_good, 4),
                    quantity_bad = Math.Round(x.quantity_bad, 4)
                })
                .ToList();
        }

        private static string? NormalizeTokenText(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var text = value.Trim();

            if (maxLength > 0 && text.Length > maxLength)
                text = text[..maxLength];

            return text;
        }

        private static ulong ToScaledUInt64(decimal value)
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Value must be >= 0");

            var scaled = decimal.Round(
                value * 10000m,
                0,
                MidpointRounding.AwayFromZero);

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

        private static void WriteNullableString(Stream stream, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                WriteVarUInt(stream, 0);
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(value.Trim());

            /*
             * length + 1 để phân biệt null và chuỗi rỗng.
             */
            WriteVarUInt(stream, (ulong)bytes.Length + 1);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static string? ReadNullableString(Stream stream)
        {
            var encodedLength = ReadVarUInt(stream);

            if (encodedLength == 0)
                return null;

            var length = checked((int)(encodedLength - 1));

            if (length == 0)
                return "";

            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, length);

            if (read != length)
                throw new EndOfStreamException("Unexpected end of token string");

            return Encoding.UTF8.GetString(buffer);
        }

        private static byte[] BrotliCompress(byte[] input)
        {
            using var output = new MemoryStream();

            using (var brotli = new BrotliStream(
                       output,
                       CompressionLevel.Optimal,
                       leaveOpen: true))
            {
                brotli.Write(input, 0, input.Length);
            }

            return output.ToArray();
        }

        private static byte[] BrotliDecompress(byte[] input)
        {
            using var source = new MemoryStream(input);
            using var brotli = new BrotliStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream();

            brotli.CopyTo(output);

            return output.ToArray();
        }

        private static Dictionary<char, int> BuildBase32Map()
        {
            var map = Base32Alphabet
                .Select((ch, idx) => new { ch, idx })
                .ToDictionary(x => x.ch, x => x.idx);

            /*
             * Decode tolerant:
             * - Máy/người nhập O thì hiểu là 0.
             * - I/L thì hiểu là 1.
             * Token generate ra sẽ không bao giờ dùng O/I/L.
             */
            map['O'] = map['0'];
            map['I'] = map['1'];
            map['L'] = map['1'];

            return map;
        }
    }
}