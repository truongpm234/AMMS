using AMMS.Shared.DTOs.Productions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface ITaskQrTokenService
    {
        
        string CreateToken(
        int taskId,
        int qtyGood,
        IReadOnlyList<TaskMaterialUsageInputDto>? materials,
        TimeSpan ttl,
        bool useManualInput,
        string? reason,
        string? reportImageUrl,
        IReadOnlyList<TaskReferenceUsageInputDto>? referenceInputs,
        IReadOnlyList<TaskOutputReportDto>? outputs);
        bool TryValidate(string token, out int taskId, out int qtyGood, out string reason);
        bool TryValidate(string token, out TaskQrTokenPayloadDto payload, out string reason);
    }
}
