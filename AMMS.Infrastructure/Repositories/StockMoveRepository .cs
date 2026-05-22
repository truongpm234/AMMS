using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Repositories
{
    public class StockMoveRepository : IStockMoveRepository
    {
        private readonly AppDbContext _db;

        public StockMoveRepository(AppDbContext db)
        {
            _db = db;
        }

        public async Task<PagedResultLite<stock_move>> GetPagedAsync(
            int page,
            int pageSize,
            CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;
            if (pageSize > 200) pageSize = 200;

            var rows = await _db.stock_moves
                .AsNoTracking()
                .OrderByDescending(x => x.move_date)
                .ThenByDescending(x => x.move_id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize + 1)
                .ToListAsync(ct);

            var hasNext = rows.Count > pageSize;

            if (hasNext)
                rows.RemoveAt(rows.Count - 1);

            return new PagedResultLite<stock_move>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = rows
            };
        }
    }
}
