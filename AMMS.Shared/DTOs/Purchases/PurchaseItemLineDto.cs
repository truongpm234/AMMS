using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Purchases
{
    public sealed record PurchaseItemLineDto(
        int id,
        int material_id,
        string? material_code,
        string? material_name,
        string? unit,
        decimal qty_ordered,
        decimal? price
    );
}

