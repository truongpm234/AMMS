using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Requests
{
    public class CreateRequestResponse
    {
        public string message { get; set; } = "Create order successfully";
        public int order_request_id { get; set; }
    }
}
