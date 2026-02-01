using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities;
[Table("suppliers", Schema = "AMMS_DB")]

public partial class supplier
{
    public int supplier_id { get; set; }

    public string name { get; set; } = null!;

    public string? contact_person { get; set; }

    public string? phone { get; set; }

    public string? email { get; set; }

    public string? type { get; set; }

    [Column(TypeName = "numeric(3,2)")]
    public decimal rating { get; set; } = 0;

    public virtual ICollection<purchase> purchases { get; set; } = new List<purchase>();
    public virtual ICollection<supplier_material> supplier_materials { get; set; } = new List<supplier_material>();
}
