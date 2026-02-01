using AMMS.Shared.DTOs.Productions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface ITaskScanService
    {
        Task<ScanTaskResult> ScanFinishAsync(ScanTaskRequest req);
        Task<int> SuggestQtyGoodAsync(int taskId, CancellationToken ct = default);
    }
}
