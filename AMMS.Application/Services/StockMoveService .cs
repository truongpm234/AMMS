using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.StockMoves;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Services
{
    public class StockMoveService : IStockMoveService
    {
        private readonly IStockMoveRepository _repo;

        public StockMoveService(IStockMoveRepository repo)
        {
            _repo = repo;
        }

        public async Task<PagedResultLite<StockMoveDto>> GetPagedAsync(
            int page,
            int pageSize,
            CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;
            if (pageSize > 200) pageSize = 200;

            var raw = await _repo.GetPagedAsync(page, pageSize, ct);

            var data = raw.Data.Select(x => new StockMoveDto
            {
                move_id = x.move_id,
                material_id = x.material_id,
                type = x.type,
                qty = x.qty,
                ref_doc = x.ref_doc,
                user_id = x.user_id,
                move_date = x.move_date,
                note = x.note,

                note_vn = BuildNoteVn(x)
            }).ToList();

            return new PagedResultLite<StockMoveDto>
            {
                Page = raw.Page,
                PageSize = raw.PageSize,
                HasNext = raw.HasNext,
                Data = data
            };
        }

        private static string BuildNoteVn(stock_move x)
        {
            var type = Normalize(x.type);
            var refDoc = Normalize(x.ref_doc);
            var note = Normalize(x.note);

            if (refDoc.StartsWith("PROD_RESERVE_ROLLBACK") ||
                refDoc.StartsWith("PROD-RESERVE-ROLLBACK"))
            {
                return "Hoàn trả nguyên vật liệu đã giữ chỗ cho production.";
            }

            if (refDoc.StartsWith("PROD_RESERVE") ||
                refDoc.StartsWith("PROD-RESERVE"))
            {
                return "Xuất kho/Giữ chỗ nguyên vật liệu cho production.";
            }

            if (refDoc.StartsWith("BUY_MATERIAL") ||
                refDoc.StartsWith("BUY-MATERIAL"))
            {
                return "Nhập kho nguyên vật liệu từ nghiệp vụ mua hàng.";
            }

            if (refDoc.StartsWith("IMPORT") ||
                note.Contains("EXCEL"))
            {
                return "Nhập kho nguyên vật liệu từ file Excel.";
            }

            if (type == "IN")
            {
                return "Nhập kho nguyên vật liệu.";
            }

            if (type == "OUT")
            {
                return "Xuất kho nguyên vật liệu.";
            }

            return "Biến động kho nguyên vật liệu.";
        }

        private static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            return value
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_");
        }
    }
}
