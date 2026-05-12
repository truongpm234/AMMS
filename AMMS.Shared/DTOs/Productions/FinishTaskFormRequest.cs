using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class FinishTaskFormRequest
    {
        public string token { get; set; } = "";
        public string? reason { get; set; }
        public List<IFormFile>? images { get; set; } = new();
    }
}
