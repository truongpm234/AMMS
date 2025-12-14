using System;
using System.Collections.Generic;

namespace AMMS.Domain.Entities;

public class supplier
{
    public int supplier_id { get; set; }

    public string name { get; set; } = null!;

    public string? contact_person { get; set; }

    public string? phone { get; set; }

    public string? email { get; set; }

    public string? main_material_type { get; set; }

    public virtual ICollection<purchase> purchases { get; set; } = new List<purchase>();
}
