using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities;

[Table("task_logs", Schema = "AMMS_DB")]
public partial class task_log
{
    public int log_id { get; set; }

    public int? task_id { get; set; }

    public string? scanner_id { get; set; }

    public string? scanned_code { get; set; }

    public string? action_type { get; set; }

    public int? qty_good { get; set; }

    public int? qty_bad { get; set; }

    public int? operator_id { get; set; }

    public DateTime? log_time { get; set; }

    public virtual user? _operator { get; set; }

    public virtual task? task { get; set; }
}
