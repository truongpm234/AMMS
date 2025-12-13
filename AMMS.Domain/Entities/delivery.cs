using System;
using System.Collections.Generic;

namespace AMMS.Domain.Entities;

public partial class delivery
{
    public int delivery_id { get; set; }

    public int? order_id { get; set; }

    public DateTime? ship_date { get; set; }

    public string? carrier { get; set; }

    public string? tracking_code { get; set; }

    public string? status { get; set; }

    public virtual order? order { get; set; }
}
