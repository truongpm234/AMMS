using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities;

[Table("task_qtys", Schema = "AMMS_DB")]
public class task_qty
{
    public int id { get; set; }

    public int? task_log_id { get; set; }

    public int group_task_id { get; set; }

    public int? single_task_id { get; set; }

    public int? single_prod_id { get; set; }

    public int order_id { get; set; }

    public string? process_code { get; set; }

    public int qty_good { get; set; }

    [Column(TypeName = "jsonb")]
    public string? output_json { get; set; }

    public DateTime? created_at { get; set; }
}