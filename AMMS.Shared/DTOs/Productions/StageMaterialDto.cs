using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class StageMaterialDto
    {
        public string name { get; set; } = string.Empty;

        public string? code { get; set; }

        public decimal quantity { get; set; }

        public string unit { get; set; } = string.Empty;
    }
}

