using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Productions;
using AMMS.Shared.DTOs.SubProduct;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface ISubProductService
    {
        Task<SubProductDto?> GetByIdAsync(int id, CancellationToken ct = default);

        Task<PagedResultLite<SubProductDto>> GetPagedAsync(
            int page,
            int pageSize,
            bool? isActive = null,
            bool? isImported = null,
            CancellationToken ct = default);

        Task<CreateSubProductResponse> CreateAsync(
            CreateSubProductDto dto,
            CancellationToken ct = default);

        Task<UpdateSubProductResponse> UpdateAsync(
            int id,
            UpdateSubProductDto dto,
            CancellationToken ct = default);

        Task<DeleteSubProductResponse> DeleteAsync(
            int id,
            CancellationToken ct = default);

        Task<SubProductImportReceiptBatchResponseDto> GenerateImportReceiptsAsync(
            List<int> subProductIds,
            CancellationToken ct = default);

        Task<ImportPendingSubProductsResponseDto> ImportPendingSubProductsAsync(
            List<int>? ids = null,
            CancellationToken ct = default);
    }
}
