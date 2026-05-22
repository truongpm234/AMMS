using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Interfaces
{
    public interface IStockMoveRepository
    {
        Task<PagedResultLite<stock_move>> GetPagedAsync(int page, int pageSize, CancellationToken ct = default);
    }
}
