using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities;
//[Table("stock_moves", Schema = "AMMS_DB")]
public partial class stock_move
{
    public int move_id { get; set; }

    public int? material_id { get; set; }

    public string? type { get; set; }

    public decimal? qty { get; set; }

    public string? ref_doc { get; set; }

    public int? user_id { get; set; }

    public DateTime? move_date { get; set; }

    public string? note { get; set; }

    public int? purchase_id { get; set; }

    public virtual material? material { get; set; }

    public virtual user? user { get; set; }

    public virtual purchase? purchase { get; set; }
}
