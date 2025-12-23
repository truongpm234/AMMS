using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities;
[Table("materials", Schema = "AMMS_DB")]
public partial class material
{
    public int material_id { get; set; }

    public string code { get; set; } = null!;

    public string name { get; set; } = null!;

    public string unit { get; set; } = null!;

    public decimal? stock_qty { get; set; }

    public decimal? min_stock { get; set; }

    public decimal? cost_price { get; set; }

    public string? description { get; set; }

    public int? sheet_width_mm { get; set; }

    public int? sheet_height_mm { get; set; }

    public int? sheet_length_mm { get; set; }

    public string? main_material_type { get; set; }

    public virtual ICollection<bom> boms { get; set; } = new List<bom>();

    public virtual ICollection<purchase_item> purchase_items { get; set; } = new List<purchase_item>();

    public virtual ICollection<stock_move> stock_moves { get; set; } = new List<stock_move>();
}
