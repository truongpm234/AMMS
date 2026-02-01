using AMMS.Infrastructure.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Interfaces
{
    public interface ITaskRepository
    {
        Task AddRangeAsync(IEnumerable<task> tasks);
        Task SaveChangesAsync();
        Task<task?> GetByIdAsync(int taskId);
        Task<task?> GetNextTaskAsync(int prodId, int currentSeqNum);
        Task<task?> GetPrevTaskAsync(int prodId, int seqNum);
        Task<int> SuggestQtyGoodAsync(int taskId, CancellationToken ct = default);
    }
}
