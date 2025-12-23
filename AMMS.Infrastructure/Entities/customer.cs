using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities;
[Table("customers", Schema = "AMMS_DB")]
public partial class customer
{
    public int customer_id { get; set; }

    public int? user_id { get; set; }

    public string? company_name { get; set; }

    public string? contact_name { get; set; }

    public string? phone { get; set; }

    public string? email { get; set; }

    public string? address { get; set; }

    public virtual ICollection<order> orders { get; set; } = new List<order>();

    public virtual ICollection<quote> quotes { get; set; } = new List<quote>();

    public virtual user? user { get; set; }
}
