using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
namespace AMMS.Shared.DTOs.Productions
{
    public class ForceSetProductionImportingByProdIdResponse
    {
        public bool success { get; set; }

        public int prod_id { get; set; }

        public int? order_id { get; set; }

        public string? prod_kind { get; set; }

        public string? prod_method { get; set; }

        public string? production_status { get; set; }

        public string? order_status { get; set; }

        public bool all_tasks_finished { get; set; }

        public bool order_full_path_finished { get; set; }

        public string message { get; set; } = "";
    }
}