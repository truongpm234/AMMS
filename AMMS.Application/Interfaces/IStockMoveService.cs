using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.StockMoves;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IStockMoveService
    {
        Task<PagedResultLite<StockMoveDto>> GetPagedAsync(int page, int pageSize, CancellationToken ct = default);
    }
}
