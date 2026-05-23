using System;
using System.Collections.Generic;
using System.Linq;

namespace AMMS.Application.Helpers
{
    public static class GroupProductionHelper
    {
        private static readonly HashSet<string> ShareableCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "PHU",
            "CAN",
            "BOI"
        };

        public static string Norm(string? code)
        {
            return (code ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        public static List<string> ParseCodes(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();

            return raw
                .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Norm)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string BuildProcessKey(string? raw)
        {
            return string.Join(",", ParseCodes(raw));
        }

        public static bool IsShareable(string? code)
        {
            return ShareableCodes.Contains(Norm(code));
        }

        public static void EnsureShareableCodes(IEnumerable<string> codes)
        {
            var invalid = codes
                .Select(Norm)
                .Where(x => !IsShareable(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (invalid.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Chỉ được ghép các công đoạn PHU,CAN,CAN_MANG,BOI. Không hợp lệ: {string.Join(",", invalid)}");
            }
        }
    }
}