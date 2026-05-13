using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities;
[Table("productions", Schema = "AMMS_DB")]

public partial class production
{
    public int prod_id { get; set; }

    public string? code { get; set; }

    public int? order_id { get; set; }

    public int? manager_id { get; set; }

    public DateTime? created_at { get; set; }

    public DateTime? planned_start_date { get; set; }

    public DateTime? actual_start_date { get; set; }

    public DateTime? end_date { get; set; }

    public string? status { get; set; }

    public int? product_type_id { get; set; }

    public string? note { get; set; }

    public bool? is_full_process { get; set; }

    public int? sub_product_id { get; set; }

    public int sub_product_used_qty { get; set; } = 0;

    public string? prod_method { get; set; }

    public string? gm_note { get; set; }

    public string? mgr_note { get; set; }

    public string? gm_proposed_method { get; set; }

    public int nvl_qty { get; set; } = 0;

    public string prod_kind { get; set; } = "SINGLE";

    public string? group_process_codes { get; set; }

    public int group_total_qty { get; set; } = 0;

    public virtual ICollection<prod_order> prod_orders { get; set; } = new List<prod_order>();

    public virtual sub_product? sub_product { get; set; }

    public virtual user? manager { get; set; }

    public virtual order? order { get; set; }

    public virtual product_type? product_type { get; set; }

    public string? import_recieve_path { get; set; }

    public virtual ICollection<task> tasks { get; set; } = new List<task>();
}
