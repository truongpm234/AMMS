using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class CustomerContractByOrderResponse
    {
        public int order_id { get; set; }

        public int? order_quote_id { get; set; }

        public int? order_request_id { get; set; }

        public string? customer_signed_contract_path { get; set; }
    }

    public class CustomerContractCandidateDto
    {
        public int estimate_id { get; set; }

        public int order_request_id { get; set; }

        public bool is_active { get; set; }

        public DateTime created_at { get; set; }

        public string? consultant_contract_path { get; set; }

        public string? customer_signed_contract_path { get; set; }

        public bool is_accepted_estimate { get; set; }

        public bool is_quote_estimate { get; set; }

        public bool has_customer_contract { get; set; }

        public bool has_consultant_contract { get; set; }
    }
}
