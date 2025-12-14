using System;
using System.Collections.Generic;

namespace AMMS.Domain.Entities;

public class purchase
{
    public int purchase_id { get; set; }

    public string? code { get; set; }

    public int? supplier_id { get; set; }

    public int? created_by { get; set; }

    public string? status { get; set; }

    public DateTime? eta_date { get; set; }

    public DateTime? created_at { get; set; }

    public virtual user? created_byNavigation { get; set; }

    public virtual ICollection<purchase_item> purchase_items { get; set; } = new List<purchase_item>();

    public virtual supplier? supplier { get; set; }
}
