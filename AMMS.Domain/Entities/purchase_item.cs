using System;
using System.Collections.Generic;

namespace AMMS.Domain.Entities;

public class purchase_item
{
    public int id { get; set; }

    public int? purchase_id { get; set; }

    public int? material_id { get; set; }

    public decimal? qty_ordered { get; set; }

    public decimal? price { get; set; }

    public virtual material? material { get; set; }

    public virtual purchase? purchase { get; set; }
}
