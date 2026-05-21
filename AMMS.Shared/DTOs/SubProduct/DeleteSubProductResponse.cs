using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.SubProduct
{
    public class DeleteSubProductResponse
    {
        public bool success { get; set; }

        public int id { get; set; }

        public string message { get; set; } = "";
    }
}
