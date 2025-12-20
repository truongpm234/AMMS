using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities;

[Table("tasks", Schema = "AMMS_DB")]

public partial class task
{
    public int task_id { get; set; }

    public int? prod_id { get; set; }

    public string name { get; set; } = null!;

    public int? seq_num { get; set; }

    public string? status { get; set; }

    public int? assigned_to { get; set; }

    public string? machine { get; set; }

    public DateTime? start_time { get; set; }

    public DateTime? end_time { get; set; }

    public int? process_id { get; set; }

    public virtual user? assigned_toNavigation { get; set; }

    public virtual product_type_process? process { get; set; }

    public virtual production? prod { get; set; }

    public virtual ICollection<task_log> task_logs { get; set; } = new List<task_log>();
}
