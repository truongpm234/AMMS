using System;
using System.Collections.Generic;

namespace AMMS.Infrastructure.Entities;

public partial class quote
{
    public int quote_id { get; set; }

    public int? customer_id { get; set; }

    public int? consultant_id { get; set; }

    public decimal? total_amount { get; set; }

    public string? status { get; set; }

    public DateTime? created_at { get; set; }

    public virtual user? consultant { get; set; }

    public virtual customer? customer { get; set; }

    public virtual ICollection<order> orders { get; set; } = new List<order>();
}
