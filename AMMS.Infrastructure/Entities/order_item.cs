using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities;

//[Table("order_items", Schema = "AMMS_DB")]
public partial class order_item
{
    public int item_id { get; set; }

    public int? order_id { get; set; }

    public string? product_name { get; set; }

    public int quantity { get; set; }

    public string? finished_size { get; set; }

    public string? print_size { get; set; }

    public string? paper_type { get; set; }

    public string? colors { get; set; }

    public string? post_processing { get; set; }

    public string? design_url { get; set; }

    public int? product_type_id { get; set; }

    public virtual ICollection<bom> boms { get; set; } = new List<bom>();

    public virtual order? order { get; set; }

    public virtual product_type? product_type { get; set; }
}
