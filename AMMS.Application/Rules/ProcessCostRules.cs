using AMMS.Shared.DTOs.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Rules
{
    public static class ProcessCostRules
    {
        public static (decimal unitPrice, string unit, string note) GetRate(ProcessType p)
        {
            return p switch
            {
                ProcessType.IN => (2500m, "m2", "Giá công in theo m2 bản in"),
                ProcessType.RALO => (80m, "sheet", "Ralo theo tờ"),
                ProcessType.CAT => (0m, "sheet", "Không tính (nếu muốn tính thì cập nhật)"),
                ProcessType.BOI => (200m, "sheet", "Bồi theo tờ"),
                ProcessType.PHU => (500m, "m2", "Công phủ theo m2"),
                ProcessType.CAN_MANG => (700m, "m2", "Công cán màng theo m2"),
                ProcessType.BE => (150m, "sheet", "Công bế theo tờ"),
                ProcessType.DUT => (0m, "sheet", "Tính chung với bế"),
                ProcessType.DAN => (300m, "box", "Công dán theo hộp"),
                ProcessType.DOT => (0m, "box", "Chưa cấu hình giá"),
                _ => (0m, "unit", "Chưa cấu hình")
            };
        }
    }
}
