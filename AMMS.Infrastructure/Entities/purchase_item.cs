using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities;
[Table("purchase_items", Schema = "AMMS_DB")]
public partial class purchase_item
{
    public int id { get; set; }

    public int? purchase_id { get; set; }

    public int? material_id { get; set; }

    public decimal? qty_ordered { get; set; }

    public decimal? price { get; set; }

    public string? unit { get; set; }

    public string? material_code { get; set; }

    public string? material_name { get; set; }

    public virtual material? material { get; set; }

    public virtual purchase? purchase { get; set; }
}
