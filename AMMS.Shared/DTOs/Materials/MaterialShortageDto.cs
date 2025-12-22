using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Materials
{
    public record MaterialShortageDto(
        int MaterialId,
        string Code,
        string Name,
        string Unit,
        decimal StockQty,
        decimal RequiredQty,
        decimal ShortageQty,
        decimal NeedToBuyQty
    );
}
