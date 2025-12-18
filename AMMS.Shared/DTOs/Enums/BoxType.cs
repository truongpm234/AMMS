using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Enums
{
    public enum BoxType
    {
        STANDARD,      // Hộp cơ bản có nắp và đáy
        ONE_SIDE,      // Hộp 1 chiều (chỉ nắp hoặc đáy)
        TRAY,          // Hộp dạng khay (cho gạch)
        TWO_PIECE,     // Hộp 2 mảnh (thân + nắp rời)
        SLEEVE         // Hộp dạng túi
    }
}
