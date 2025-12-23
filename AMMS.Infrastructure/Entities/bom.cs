using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities;
<<<<<<< HEAD
//[Table("boms", Schema = "AMMS_DB")]

=======
[Table("boms", Schema = "AMMS_DB")]
>>>>>>> main
public partial class bom
{
    public int bom_id { get; set; }

    public int? order_item_id { get; set; }

    public int? material_id { get; set; }

    public decimal? qty_per_product { get; set; }

    public decimal? wastage_percent { get; set; }

    public virtual material? material { get; set; }

    public virtual order_item? order_item { get; set; }
}
