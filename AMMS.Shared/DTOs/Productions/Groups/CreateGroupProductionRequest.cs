using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions.Groups
{
    public class CreateGroupProductionRequest
    {
        public List<int> order_ids { get; set; } = new();

        // Ví dụ: ["PHU", "CAN", "BOI", "BE", "DUT", "DAN"]
        public List<string> process_codes { get; set; } = new();

        public bool? is_priority { get; set; } = false;

        public string? note { get; set; }
    }
}
