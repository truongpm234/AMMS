using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities
{
    [Table("task_links", Schema = "AMMS_DB")]
    public class task_link
    {
        public int id { get; set; }

        public int group_prod_id { get; set; }

        public int group_task_id { get; set; }

        public int single_prod_id { get; set; }

        public int? single_task_id { get; set; }

        public int? original_single_task_id { get; set; }

        public DateTime? done_at { get; set; }

        public int order_id { get; set; }

        public string? process_code { get; set; }

        public int qty_plan { get; set; }

        public string status { get; set; } = "Waiting";

        public DateTime? created_at { get; set; }

        [ForeignKey(nameof(group_prod_id))]
        public production? group_prod { get; set; }

        [ForeignKey(nameof(group_task_id))]
        public task? group_task { get; set; }

        [ForeignKey(nameof(single_prod_id))]
        public production? single_prod { get; set; }

        [ForeignKey(nameof(single_task_id))]
        public task? single_task { get; set; }

        [ForeignKey(nameof(order_id))]
        public order? order { get; set; }
    }
}