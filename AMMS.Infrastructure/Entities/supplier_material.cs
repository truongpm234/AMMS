using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities;

[Table("supplier_materials", Schema = "AMMS_DB")]
public partial class supplier_material
{
    public int supplier_id { get; set; }

    public int material_id { get; set; }

    public bool is_active { get; set; } = true;

    public DateTime created_at { get; set; }

    public string? note { get; set; }

    [Column(TypeName = "numeric(18,2)")]
    public decimal? unit_price { get; set; }

    public virtual supplier supplier { get; set; } = null!;

    public virtual material material { get; set; } = null!;
}

