using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.Helpers
{
    public static class ProductionApprovalFlowHelper
    {
        public const string AutoSingleOption = "AUTO_SINGLE_OPTION";
        public const string WaitingManager = "WAITING_MANAGER";
        public const string ManualManager = "MANUAL_MANAGER";
        public const string ManualGeneralManager = "MANUAL_GENERAL_MANAGER";

        public static bool IsAuto(string? value)
        {
            return string.Equals(
                value,
                AutoSingleOption,
                StringComparison.OrdinalIgnoreCase);
        }

        public static string? Label(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return value.Trim().ToUpperInvariant() switch
            {
                AutoSingleOption => "Hệ thống tự duyệt do chỉ có 1 phương thức sản xuất hợp lệ",
                WaitingManager => "Đang chờ manager duyệt phương thức sản xuất",
                ManualManager => "Duyệt thủ công",
                ManualGeneralManager => "Duyệt thủ công",
                _ => value
            };
        }
    }
}
