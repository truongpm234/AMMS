using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    [JsonConverter(typeof(TaskMaterialUsageInputDtoJsonConverter))]
    public class TaskMaterialUsageInputDto
    {
        public int material_id { get; set; }

        public string? material_code { get; set; }

        public string? material_name { get; set; }

        public string? unit { get; set; }

        /*
         * GIỮ NGUYÊN tên property nội bộ.
         * Core flow hiện tại vẫn dùng quantity_used / quantity_left.
         * Chỉ đổi tên khi serialize/deserialize JSON bằng converter.
         */
        public decimal quantity_used { get; set; }

        public decimal quantity_left { get; set; }

        public bool is_stock { get; set; }
    }

    public sealed class TaskMaterialUsageInputDtoJsonConverter
        : JsonConverter<TaskMaterialUsageInputDto>
    {
        public override TaskMaterialUsageInputDto Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return new TaskMaterialUsageInputDto();

            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("TaskMaterialUsageInputDto phải là object JSON.");

            var result = new TaskMaterialUsageInputDto();

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return result;

                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException("JSON materials_json không hợp lệ.");

                var propName = reader.GetString() ?? "";
                reader.Read();

                switch (NormalizeJsonName(propName))
                {
                    case "material_id":
                    case "materialid":
                        result.material_id = ReadIntFlexible(ref reader);
                        break;

                    case "material_code":
                    case "materialcode":
                        result.material_code = ReadStringFlexible(ref reader);
                        break;

                    case "material_name":
                    case "materialname":
                        result.material_name = ReadStringFlexible(ref reader);
                        break;

                    case "unit":
                    case "material_unit":
                    case "materialunit":
                        result.unit = ReadStringFlexible(ref reader);
                        break;

                    /*
                     * NEW NAME FE sẽ gửi:
                     * mat_quantity_used
                     *
                     * OLD NAME vẫn đọc được:
                     * quantity_used
                     * material_quantity_used
                     */
                    case "mat_quantity_used":
                    case "matquantityused":
                    case "quantity_used":
                    case "quantityused":
                    case "material_quantity_used":
                    case "materialquantityused":
                        result.quantity_used = ReadDecimalFlexible(ref reader);
                        break;

                    /*
                     * NEW NAME FE sẽ gửi:
                     * mat_quantity_left
                     *
                     * OLD NAME vẫn đọc được:
                     * quantity_left
                     * material_quantity_left
                     */
                    case "mat_quantity_left":
                    case "matquantityleft":
                    case "quantity_left":
                    case "quantityleft":
                    case "material_quantity_left":
                    case "materialquantityleft":
                        result.quantity_left = ReadDecimalFlexible(ref reader);
                        break;

                    case "is_stock":
                    case "isstock":
                    case "material_is_stock":
                    case "materialisstock":
                        result.is_stock = ReadBoolFlexible(ref reader);
                        break;

                    default:
                        SkipValue(ref reader);
                        break;
                }
            }

            throw new JsonException("JSON materials_json kết thúc không hợp lệ.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            TaskMaterialUsageInputDto value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            writer.WriteNumber("material_id", value.material_id);

            if (!string.IsNullOrWhiteSpace(value.material_code))
                writer.WriteString("material_code", value.material_code);

            if (!string.IsNullOrWhiteSpace(value.material_name))
                writer.WriteString("material_name", value.material_name);

            if (!string.IsNullOrWhiteSpace(value.unit))
                writer.WriteString("unit", value.unit);

            /*
             * JSON public mới.
             */
            writer.WriteNumber("mat_quantity_used", Math.Round(value.quantity_used, 4));
            writer.WriteNumber("mat_quantity_left", Math.Round(value.quantity_left, 4));

            writer.WriteBoolean("is_stock", value.is_stock);

            writer.WriteEndObject();
        }

        private static string NormalizeJsonName(string? value)
        {
            return (value ?? "")
                .Trim()
                .Replace("-", "_")
                .ToLowerInvariant();
        }

        private static string? ReadStringFlexible(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;

            if (reader.TokenType == JsonTokenType.String)
                return reader.GetString();

            if (reader.TokenType == JsonTokenType.Number)
            {
                if (reader.TryGetDecimal(out var decimalValue))
                    return decimalValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

                if (reader.TryGetInt64(out var longValue))
                    return longValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

                return null;
            }

            if (reader.TokenType == JsonTokenType.True)
                return "true";

            if (reader.TokenType == JsonTokenType.False)
                return "false";

            SkipValue(ref reader);
            return null;
        }

        private static int ReadIntFlexible(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.Number &&
                reader.TryGetInt32(out var value))
            {
                return value;
            }

            if (reader.TokenType == JsonTokenType.String &&
                int.TryParse(reader.GetString(), out var parsed))
            {
                return parsed;
            }

            SkipValue(ref reader);
            return 0;
        }

        private static decimal ReadDecimalFlexible(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.Number &&
                reader.TryGetDecimal(out var value))
            {
                return Math.Round(value, 4);
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                var raw = reader.GetString();

                if (!string.IsNullOrWhiteSpace(raw))
                {
                    raw = raw.Replace(",", ".");

                    if (decimal.TryParse(
                            raw,
                            System.Globalization.NumberStyles.Number,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var parsed))
                    {
                        return Math.Round(parsed, 4);
                    }
                }
            }

            SkipValue(ref reader);
            return 0m;
        }

        private static bool ReadBoolFlexible(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.True)
                return true;

            if (reader.TokenType == JsonTokenType.False)
                return false;

            if (reader.TokenType == JsonTokenType.Number &&
                reader.TryGetInt32(out var number))
            {
                return number == 1;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                var raw = reader.GetString();

                if (bool.TryParse(raw, out var parsedBool))
                    return parsedBool;

                if (raw == "1")
                    return true;

                if (raw == "0")
                    return false;
            }

            SkipValue(ref reader);
            return false;
        }

        private static void SkipValue(ref Utf8JsonReader reader)
        {
            try
            {
                using var _ = JsonDocument.ParseValue(ref reader);
            }
            catch
            {
                // ignore invalid extra value
            }
        }
    }
}
