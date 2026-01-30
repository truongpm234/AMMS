using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities;
[Table("quotes", Schema = "AMMS_DB")]
public partial class quote
{
    public int quote_id { get; set; }

    public int? order_request_id { get; set; }

    public int? consultant_id { get; set; }

    public decimal? total_amount { get; set; }

    public string? status { get; set; }

    public DateTime created_at { get; set; } = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    public virtual order_request? order_request { get; set; }

    public virtual user? consultant { get; set; }

    public virtual ICollection<order> orders { get; set; } = new List<order>();
}
