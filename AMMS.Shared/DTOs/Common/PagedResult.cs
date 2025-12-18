using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Common
{
    public class PagedResultLite<T>
    {
        public int Page { get; set; }
        public int PageSize { get; set; }
        public bool HasNext { get; set; }
        public List<T> Data { get; set; } = new();
    }
}
