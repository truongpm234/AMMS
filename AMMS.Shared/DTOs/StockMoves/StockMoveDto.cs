using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.StockMoves
{
    public class StockMoveDto
    {
        public int move_id { get; set; }
        public int? material_id { get; set; }
        public string? type { get; set; }
        public decimal? qty { get; set; }
        public string? ref_doc { get; set; }
        public int? user_id { get; set; }
        public DateTime? move_date { get; set; }
        public string? note { get; set; }
        public string note_vn { get; set; } = "";
    }
}
