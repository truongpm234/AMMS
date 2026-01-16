using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Requests
{
    public class CreateResquestConsultant
    {
        public string? customer_name { get; set; }

        public string? customer_phone { get; set; }

        public string? customer_email { get; set; }

        public string? detail_address { get; set; }

    }
}

