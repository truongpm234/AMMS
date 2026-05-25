using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Email;
using AMMS.Shared.DTOs.Estimates;
using AMMS.Shared.DTOs.Requests;
using AMMS.Shared.Helpers;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.AspNetCore.Hosting;
using System.Globalization;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text;

namespace AMMS.Application.Services
{
    public class EstimateService : IEstimateService
    {
        private readonly ICostEstimateRepository _estimateRepo;
        private readonly IQuoteRepository _quoteRepo;
        private readonly IAccessService _accessService;
        private readonly ICloudinaryFileStorageService _cloudinaryStorage;
        private readonly IRequestRepository _requestRepository;
        private readonly IOrderRepository _orderRepo;
        private readonly IMaterialRepository _materialRepo;
        private readonly IBomRepository _bomRepo;
        private readonly IContractCompareService _contractCompareService;
        private readonly IEstimateConfigRepository _estimateConfigRepo;
        private readonly IWebHostEnvironment _env;

        public EstimateService(
            ICostEstimateRepository costEstimateRepository,
            IQuoteRepository quoteRepo,
            IAccessService accessService,
            ICloudinaryFileStorageService cloudinaryStorage,
            IRequestRepository requestRepository,
            IOrderRepository orderRepo,
            IMaterialRepository materialRepo,
            IBomRepository bomRepo,
            IContractCompareService contractCompareService, IEstimateConfigRepository estimateConfigRepository, IWebHostEnvironment env)
        {
            _estimateRepo = costEstimateRepository;
            _quoteRepo = quoteRepo;
            _accessService = accessService;
            _cloudinaryStorage = cloudinaryStorage;
            _requestRepository = requestRepository;
            _orderRepo = orderRepo;
            _materialRepo = materialRepo;
            _bomRepo = bomRepo;
            _contractCompareService = contractCompareService;
            _estimateConfigRepo = estimateConfigRepository;
            _env = env;
        }

        public async Task UpdateFinalCostAsync(int orderRequestId, decimal? finalCostInput)
        {
            var estimate = await _estimateRepo.GetByOrderRequestIdAsync(orderRequestId)
                ?? throw new Exception("Estimate not found for this order_request_id");

            if (finalCostInput is null)
                throw new ArgumentException("final_total_cost is required");

            var finalCost = finalCostInput.Value;

            if (finalCost < 0m)
                throw new ArgumentException("final_total_cost must be >= 0");

            estimate.final_total_cost = RoundToThousand(finalCost);

            await _estimateRepo.SaveChangesAsync();
        }

        public async Task<cost_estimate?> GetTrackingByIdAsync(int estimateId, CancellationToken ct = default)
        {
            return await _estimateRepo.GetTrackingByIdAsync(estimateId, ct);
        }

        private static decimal RoundToThousand(decimal value)
        {
            return Math.Round(value / 1000m, MidpointRounding.AwayFromZero) * 1000m;
        }

        public async Task<cost_estimate?> GetEstimateByIdAsync(int estimateId)
        {
            return await _estimateRepo.GetByIdAsync(estimateId);
        }

        public Task<DepositByRequestResponse?> GetDepositByRequestIdAsync(int requestId, CancellationToken ct = default)
            => _estimateRepo.GetDepositByRequestIdAsync(requestId, ct);

        public async Task<bool> OrderRequestExistsAsync(int orderRequestId)
        {
            return await _estimateRepo.OrderRequestExistsAsync(orderRequestId);
        }

        public async Task<int> SaveFeCostEstimateAsync(CostEstimateInsertRequest req, CancellationToken ct = default)
        {
            if (req.order_request_id <= 0)
                throw new ArgumentException("order_request_id must be > 0");

            var orderReq = await _estimateRepo.GetOrderRequestTrackingAsync(req.order_request_id, ct);
            if (orderReq == null)
                throw new Exception("Order request not found");

            cost_estimate? previousEstimate = null;

            if (req.previous_estimate_id.HasValue)
            {
                previousEstimate = await _estimateRepo.GetByIdAsync(req.previous_estimate_id.Value);
                if (previousEstimate == null)
                    throw new ArgumentException("previous_estimate_id not found");

                if (previousEstimate.order_request_id != req.order_request_id)
                    throw new ArgumentException("previous_estimate_id must belong to the same order_request_id");
            }

            var now = AppTime.NowVnUnspecified();

            var entity = new cost_estimate
            {
                order_request_id = req.order_request_id,
                previous_estimate_id = previousEstimate?.estimate_id,
                created_at = ToUnspecified(req.created_at ?? now),
                is_active = true,
                estimated_finish_date = ToUnspecified(req.estimated_finish_date ?? now),
                production_processes = req.production_processes?.Trim(),
                desired_delivery_date = ToUnspecified(req.desired_delivery_date ?? (orderReq.delivery_date ?? now)),
            };

            static void SetIfHasValue<T>(T? value, Action<T> setter) where T : struct
            {
                if (value.HasValue) setter(value.Value);
            }

            static void SetIfNotNull(string? value, Action<string> setter)
            {
                if (value != null) setter(value);
            }
            var resolvedCoatingMaterialCode = ResolveCoatingMaterialCode(
    req.coating_material_code,
    req.coating_type);

            if (string.IsNullOrWhiteSpace(resolvedCoatingMaterialCode) &&
                !string.IsNullOrWhiteSpace(previousEstimate?.coating_material_code))
            {
                resolvedCoatingMaterialCode = previousEstimate.coating_material_code;
            }

            var hasPhu = HasEstimateProcess(
                req.production_processes ?? previousEstimate?.production_processes,
                "PHU");

            var hasCoatingQty =
                (req.coating_glue_weight_kg ?? 0m) > 0m ||
                (req.coating_glue_cost ?? 0m) > 0m;

            if ((hasPhu || hasCoatingQty) && string.IsNullOrWhiteSpace(resolvedCoatingMaterialCode))
            {
                throw new ArgumentException(
                    "Khi có công đoạn PHU hoặc có keo phủ, phải truyền coating_material_code hoặc coating_type hợp lệ.");
            }

            entity.coating_material_code = resolvedCoatingMaterialCode;

            var laminationMaterialIdInput =
    req.lamination_material_id ?? previousEstimate?.lamination_material_id;

            var laminationMaterialCodeInput =
                !string.IsNullOrWhiteSpace(req.lamination_material_code)
                    ? req.lamination_material_code.Trim()
                    : previousEstimate?.lamination_material_code;

            var laminationMaterialNameInput =
                !string.IsNullOrWhiteSpace(req.lamination_material_name)
                    ? req.lamination_material_name.Trim()
                    : previousEstimate?.lamination_material_name;

            var laminationSnapshot = await EstimateMaterialSnapshotHelper.ResolveAsync(
                _materialRepo,
                laminationMaterialIdInput,
                laminationMaterialCodeInput,
                laminationMaterialNameInput,
                defaultName: "Màng cán",
                defaultUnit: "kg");

            if ((req.lamination_weight_kg ?? 0m) > 0m && laminationSnapshot?.material_id == null)
            {
                throw new ArgumentException(
                    "Khi có màng cán, phải truyền lamination_material_id hoặc lamination_material_code hợp lệ.");
            }

            if (laminationSnapshot != null)
            {
                entity.lamination_material_id = laminationSnapshot.material_id;
                entity.lamination_material_code = laminationSnapshot.code;
                entity.lamination_material_name = laminationSnapshot.name;
            }
            else
            {
                entity.lamination_material_id = null;
                entity.lamination_material_code = null;
                entity.lamination_material_name = null;
            }

            SetIfHasValue(req.paper_cost, v => entity.paper_cost = v);
            SetIfHasValue(req.paper_sheets_used, v => entity.paper_sheets_used = v);
            SetIfHasValue(req.paper_unit_price, v => entity.paper_unit_price = v);

            SetIfHasValue(req.ink_cost, v => entity.ink_cost = v);
            SetIfHasValue(req.ink_weight_kg, v => entity.ink_weight_kg = v);
            SetIfHasValue(req.ink_rate_per_m2, v => entity.ink_rate_per_m2 = v);
            SetIfNotNull(req.ink_type_names, v => entity.ink_type_names = v.Trim());

            SetIfHasValue(req.coating_glue_cost, v => entity.coating_glue_cost = v);
            SetIfHasValue(req.coating_glue_weight_kg, v => entity.coating_glue_weight_kg = v);
            SetIfHasValue(req.coating_glue_rate_per_m2, v => entity.coating_glue_rate_per_m2 = v);
            SetIfNotNull(req.paper_code, v => entity.paper_code = v);
            SetIfNotNull(req.paper_name, v => entity.paper_name = v);
            SetIfNotNull(req.coating_type, v => entity.coating_type = v.Trim());
            SetIfNotNull(req.wave_type, v => entity.wave_type = v);
            SetIfHasValue(req.wave_sheets_used, v => entity.wave_sheets_used = v);

            SetIfHasValue(req.mounting_glue_cost, v => entity.mounting_glue_cost = v);
            SetIfHasValue(req.mounting_glue_weight_kg, v => entity.mounting_glue_weight_kg = v);
            SetIfHasValue(req.mounting_glue_rate_per_m2, v => entity.mounting_glue_rate_per_m2 = v);

            SetIfHasValue(req.lamination_cost, v => entity.lamination_cost = v);
            SetIfHasValue(req.lamination_weight_kg, v => entity.lamination_weight_kg = v);
            SetIfHasValue(req.lamination_rate_per_m2, v => entity.lamination_rate_per_m2 = v);

            SetIfHasValue(req.material_cost, v => entity.material_cost = v);
            SetIfHasValue(req.base_cost, v => entity.base_cost = v);

            if (req.is_rush.HasValue) entity.is_rush = req.is_rush.Value;
            SetIfHasValue(req.rush_percent, v => entity.rush_percent = v);
            SetIfHasValue(req.rush_amount, v => entity.rush_amount = v);
            SetIfHasValue(req.days_early, v => entity.days_early = v);

            SetIfHasValue(req.subtotal, v => entity.subtotal = v);
            SetIfHasValue(req.discount_percent, v => entity.discount_percent = v);
            SetIfHasValue(req.discount_amount, v => entity.discount_amount = v);
            SetIfHasValue(req.final_total_cost, v => entity.final_total_cost = v);
            entity.deposit_amount = req.deposit_amount;
            SetIfHasValue(req.sheets_required, v => entity.sheets_required = v);
            SetIfHasValue(req.sheets_waste, v => entity.sheets_waste = v);
            SetIfHasValue(req.sheets_total, v => entity.sheets_total = v);
            SetIfHasValue(req.n_up, v => entity.n_up = v);
            SetIfHasValue(req.total_area_m2, v => entity.total_area_m2 = v);
            SetIfHasValue(req.waste_gluing_boxes, v => entity.waste_gluing_boxes = v);
            SetIfHasValue(req.sheet_area_m2, v => entity.sheet_area_m2 = v);
            SetIfHasValue(req.print_sheets_used, v => entity.print_sheets_used = v);
            SetIfHasValue(req.total_coating_area_m2, v => entity.total_coating_area_m2 = v);
            SetIfHasValue(req.total_lamination_area_m2, v => entity.total_lamination_area_m2 = v);
            SetIfHasValue(req.coating_sheets_used, v => entity.coating_sheets_used = v);
            SetIfHasValue(req.lamination_sheets_used, v => entity.lamination_sheets_used = v);
            SetIfHasValue(req.wave_sheet_area_m2, v => entity.wave_sheet_area_m2 = v);
            SetIfHasValue(req.wave_n_up, v => entity.wave_n_up = v);
            SetIfHasValue(req.wave_sheets_required, v => entity.wave_sheets_required = v);
            SetIfHasValue(req.total_mounting_area_m2, v => entity.total_mounting_area_m2 = v);
            SetIfHasValue(req.wave_unit_price, v => entity.wave_unit_price = v);
            SetIfHasValue(req.wave_cost, v => entity.wave_cost = v);
            SetIfHasValue(req.total_process_cost, v => entity.total_process_cost = v);
            SetIfHasValue(req.design_cost, v => entity.design_cost = v);
            SetIfNotNull(req.cost_note, v => entity.cost_note = v);

            SetIfHasValue(req.bleed_mm, v => orderReq.bleed_mm = v);
            SetIfHasValue(req.glue_tab_mm, v => orderReq.glue_tab_mm = v);
            if (req.is_one_side_box.HasValue) orderReq.is_one_side_box = req.is_one_side_box.Value;
            SetIfHasValue(req.print_length_mm, v => orderReq.print_length_mm = v);
            SetIfHasValue(req.print_width_mm, v => orderReq.print_width_mm = v);

            if (req.process_costs != null)
            {
                foreach (var p in req.process_costs)
                {
                    var pcode = (p.process_code ?? "").Trim().ToUpperInvariant();
                    var pname = string.IsNullOrWhiteSpace(p.process_name) ? pcode : p.process_name.Trim();

                    decimal qty = p.quantity ?? 0m;
                    decimal unitPrice = p.unit_price ?? 0m;
                    decimal totalCost = p.total_cost ?? 0m;

                    if (totalCost <= 0m)
                        totalCost = qty * unitPrice;

                    entity.process_costs.Add(new cost_estimate_process
                    {
                        process_code = pcode,
                        process_name = pname,
                        quantity = qty,
                        unit = string.IsNullOrWhiteSpace(p.unit) ? "" : p.unit.Trim(),
                        unit_price = unitPrice,
                        total_cost = totalCost,
                        note = p.note,
                        created_at = AppTime.NowVnUnspecified()
                    });
                }

                if (!req.total_process_cost.HasValue || req.total_process_cost.Value <= 0)
                {
                    entity.total_process_cost = entity.process_costs.Sum(x => x.total_cost);
                }
            }

            await _estimateRepo.AddAsync(entity);
            await _estimateRepo.SaveChangesAsync();
            await _estimateRepo.NormalizeActiveDraftEstimatesAsync(entity.order_request_id, entity.estimate_id, ct);
            await _requestRepository.RecalculateAndPersistAsync(entity.order_request_id, ct);
            return entity.estimate_id;
        }

        public static DateTime ToUnspecified(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Unspecified)
                return dt;

            return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        }

        public async Task<List<RequestEstimateDto>> GetAllEstimatesFlatByRequestIdAsync(int requestId, CancellationToken ct = default)
        {
            await _accessService.EnsureCanAccessAssignedRequestAsync(requestId, ct);
            return await _estimateRepo.GetAllEstimatesFlatByRequestIdAsync(requestId, ct);
        }

        public async Task<QuoteEmailComparePreviewResponse> BuildPreviewAsync(int requestId, CancellationToken ct = default)
        {
            var res = await _quoteRepo.BuildPreviewAsync(requestId, ct);

            var orderReq = await _estimateRepo.GetOrderRequestTrackingAsync(requestId, ct);
            if (orderReq != null)
            {
                if (!orderReq.estimate_finish_date.HasValue)
                {
                    orderReq.estimate_finish_date = await _requestRepository.RecalculateAndPersistAsync(requestId, ct);
                }

                res.estimate_finish_date = orderReq.estimate_finish_date;
            }

            return res;
        }

        public async Task<string> UploadConsultantContractAsync(
    int requestId,
    int estimateId,
    Stream fileStream,
    string fileName,
    string contentType,
    CancellationToken ct = default)
        {
            if (requestId <= 0) throw new ArgumentException("request_id must be > 0");
            if (estimateId <= 0) throw new ArgumentException("estimate_id must be > 0");
            if (fileStream == null) throw new ArgumentException("file is required");
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("fileName is required");

            var estimate = await _estimateRepo.GetTrackingByIdAsync(estimateId, ct);
            if (estimate == null || estimate.order_request_id != requestId)
                throw new InvalidOperationException("Estimate not found");

            var ext = Path.GetExtension(fileName)?.Trim().ToLowerInvariant();
            if (ext != ".docx")
                throw new ArgumentException("Consultant draft contract must be .docx");

            var publicId = $"contracts/request_{requestId}/estimate_{estimateId}/consultant_draft";
            var safeFileName = Path.GetFileName(fileName);

            var url = await _cloudinaryStorage.UploadRawWithPublicIdAsync(
                fileStream,
                safeFileName,
                string.IsNullOrWhiteSpace(contentType)
                    ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                    : contentType,
                publicId);

            estimate.consultant_contract_path = url;

            await _estimateRepo.SaveChangesAsync();

            return url;
        }

        public async Task<UploadCustomerSignedContractResponse> UploadCustomerSignedContractAsync(
    int requestId,
    int estimateId,
    Stream fileStream,
    string fileName,
    string contentType,
    CancellationToken ct = default)
        {
            if (requestId <= 0) throw new ArgumentException("request_id must be > 0");
            if (estimateId <= 0) throw new ArgumentException("estimate_id must be > 0");
            if (fileStream == null) throw new ArgumentException("file is required");
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("fileName is required");

            var estimate = await _estimateRepo.GetTrackingByIdAsync(estimateId, ct);
            if (estimate == null || estimate.order_request_id != requestId)
                throw new InvalidOperationException("Estimate not found");

            if (string.IsNullOrWhiteSpace(estimate.consultant_contract_path))
                throw new InvalidOperationException("Consultant contract has not been uploaded yet");

            var ext = Path.GetExtension(fileName)?.Trim().ToLowerInvariant();
            if (ext != ".pdf")
                throw new ArgumentException("Customer signed contract must be .pdf");

            byte[] pdfBytes;
            await using (var pdfMs = new MemoryStream())
            {
                if (fileStream.CanSeek) fileStream.Position = 0;
                await fileStream.CopyToAsync(pdfMs, ct);
                pdfBytes = pdfMs.ToArray();
            }

            var compareResult = await _contractCompareService.CompareAsync(
                requestId,
                estimateId,
                estimate.consultant_contract_path!,
                pdfBytes,
                ct);

            if (compareResult.similarity_percent < 95m)
            {
                return new UploadCustomerSignedContractResponse
                {
                    request_id = requestId,
                    estimate_id = estimateId,
                    customer_signed_contract_path = null,
                    compare_result = compareResult,
                    compare_warning = $"Hợp đồng khách tải lên chưa khớp so với hợp đồng tư vấn viên cung câp. Quý khách vui lòng xem lại và tải lại sau."
                };
            }

            var safeFileName = Path.GetFileName(fileName);
            var publicId = $"contracts/request_{requestId}/estimate_{estimateId}/customer_signed";

            string pdfUrl;
            await using (var uploadPdfStream = new MemoryStream(pdfBytes))
            {
                pdfUrl = await _cloudinaryStorage.UploadRawWithPublicIdAsync(
                    uploadPdfStream,
                    safeFileName,
                    string.IsNullOrWhiteSpace(contentType) ? "application/pdf" : contentType,
                    publicId);
            }

            estimate.customer_signed_contract_path = pdfUrl;
            await _estimateRepo.SaveChangesAsync();

            var request = await _requestRepository.GetByIdAsync(requestId);

            return new UploadCustomerSignedContractResponse
            {
                request_id = requestId,
                estimate_id = estimateId,
                customer_signed_contract_path = pdfUrl,
                compare_result = compareResult,
                compare_warning = null
            };
        }

        public async Task<RemainingByRequestResponse?> GetRemainingByRequestIdAsync(int requestId, CancellationToken ct = default)
        {
            var data = await _estimateRepo.GetCostSummaryByRequestIdAsync(requestId, ct);
            if (data == null) return null;

            var remaining = data.final_total_cost - data.deposit_amount;
            if (remaining < 0m) remaining = 0m;

            return new RemainingByRequestResponse
            {
                order_request_id = data.order_request_id,
                final_total_cost = data.final_total_cost,
                deposit_amount = data.deposit_amount,
                remaining_amount = remaining
            };
        }

        public async Task UpdateAlternativeMaterialsAsync(
    int requestId,
    int? estimateId,
    string? paperAlternative,
    string? waveAlternative,
    string? alternativeMaterialReason,
    CancellationToken ct = default)
        {
            var request = await _estimateRepo.GetOrderRequestTrackingAsync(requestId, ct);
            if (request == null)
                throw new InvalidOperationException("Order request not found");

            var allowedStatuses = new[] { "Accepted" };
            if (!allowedStatuses.Contains(request.process_status ?? "", StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Only Accepted request can update alternative materials");
            }

            cost_estimate? estimate = null;

            if (estimateId.HasValue && estimateId.Value > 0)
            {
                estimate = await _estimateRepo.GetTrackingByIdAsync(estimateId.Value, ct);
                if (estimate == null || estimate.order_request_id != requestId)
                    throw new InvalidOperationException("Estimate not found");
            }

            if (estimate == null && request.accepted_estimate_id.HasValue && request.accepted_estimate_id.Value > 0)
            {
                estimate = await _estimateRepo.GetTrackingByIdAsync(request.accepted_estimate_id.Value, ct);
            }

            estimate ??= await _estimateRepo.GetFirstActiveTrackingByRequestIdAsync(requestId, ct);

            if (estimate == null)
                throw new InvalidOperationException("Active/accepted estimate not found");

            if (!string.IsNullOrWhiteSpace(paperAlternative))
                estimate.paper_alternative = paperAlternative.Trim();

            if (!string.IsNullOrWhiteSpace(waveAlternative))
                estimate.wave_alternative = waveAlternative.Trim();

            if (alternativeMaterialReason != null)
                estimate.alternative_material_reason = string.IsNullOrWhiteSpace(alternativeMaterialReason) ? null : alternativeMaterialReason.Trim();

            await SyncOperationalMaterialSnapshotAsync(request, estimate, ct);

            await _estimateRepo.SaveChangesAsync();
        }

        private async Task SyncOperationalMaterialSnapshotAsync(
            order_request request,
            cost_estimate estimate,
            CancellationToken ct)
        {
            if (!request.order_id.HasValue || request.order_id.Value <= 0)
                return;

            var resolvedPaperCode = EstimateMaterialAlternativeHelper.ResolvePaperCode(
                estimate.paper_alternative,
                estimate.paper_code);

            var resolvedWaveType = EstimateMaterialAlternativeHelper.ResolveWaveType(
                estimate.wave_alternative,
                estimate.wave_type);

            material? paperMaterial = null;
            string? resolvedPaperName = estimate.paper_name;

            if (!string.IsNullOrWhiteSpace(resolvedPaperCode))
            {
                paperMaterial = await _materialRepo.GetByCodeAsync(resolvedPaperCode);
                resolvedPaperName = paperMaterial?.name ?? estimate.paper_name ?? resolvedPaperCode;
            }

            material? waveMaterial = null;
            string? resolvedWaveName = null;

            if (!string.IsNullOrWhiteSpace(resolvedWaveType))
            {
                waveMaterial = await _materialRepo.GetByCodeAsync(resolvedWaveType);
                resolvedWaveName = waveMaterial?.name ?? $"Sóng {resolvedWaveType}";
            }

            var laminationSnapshot = await EstimateMaterialSnapshotHelper.ResolveAsync(
    _materialRepo,
    estimate.lamination_material_id,
    estimate.lamination_material_code,
    estimate.lamination_material_name,
    defaultName: "Màng cán",
    defaultUnit: "kg");

            var laminationMaterial = laminationSnapshot?.material;

            var orderItems = await _orderRepo.GetOrderItemsByOrderIdAsync(request.order_id.Value, ct);

            foreach (var item in orderItems)
            {
                item.paper_code = resolvedPaperCode;
                item.paper_name = resolvedPaperName;
                item.wave_type = resolvedWaveType;
            }

            var orderItemIds = orderItems.Select(x => x.item_id).ToList();
            if (orderItemIds.Count == 0)
                return;

            var allBoms = await _bomRepo.GetByOrderItemIdsAndEstimateIdAsync(
                orderItemIds,
                estimate.estimate_id,
                ct);

            var paperCodesToMatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(estimate.paper_code))
                paperCodesToMatch.Add(estimate.paper_code.Trim());
            if (!string.IsNullOrWhiteSpace(estimate.paper_alternative))
                paperCodesToMatch.Add(estimate.paper_alternative.Trim());
            if (!string.IsNullOrWhiteSpace(resolvedPaperCode))
                paperCodesToMatch.Add(resolvedPaperCode.Trim());

            foreach (var bom in allBoms.Where(x =>
                         string.Equals((x.material_name ?? "").Trim(), "Giấy", StringComparison.OrdinalIgnoreCase) ||
                         paperCodesToMatch.Contains((x.material_code ?? "").Trim())))
            {
                bom.material_id = paperMaterial?.material_id;
                bom.material_code = EstimateHelper.Trunc20(resolvedPaperCode ?? "PAPER");
                bom.material_name = resolvedPaperName ?? "Giấy";
            }

            var waveCodesToMatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(estimate.wave_type))
                waveCodesToMatch.Add(estimate.wave_type.Trim());
            if (!string.IsNullOrWhiteSpace(estimate.wave_alternative))
                waveCodesToMatch.Add(estimate.wave_alternative.Trim());
            if (!string.IsNullOrWhiteSpace(resolvedWaveType))
                waveCodesToMatch.Add(resolvedWaveType.Trim());
            var laminationCodesToMatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(estimate.lamination_material_code))
                laminationCodesToMatch.Add(estimate.lamination_material_code.Trim());

            if (!string.IsNullOrWhiteSpace(laminationSnapshot?.code))
                laminationCodesToMatch.Add(laminationSnapshot.code.Trim());

            laminationCodesToMatch.Add("MANG_12MIC");
            laminationCodesToMatch.Add("MANG_CAN");
            laminationCodesToMatch.Add("LAMINATION");

            foreach (var bom in allBoms.Where(x =>
                         laminationCodesToMatch.Contains((x.material_code ?? "").Trim()) ||
                         string.Equals((x.material_name ?? "").Trim(), "Màng cán", StringComparison.OrdinalIgnoreCase) ||
                         (x.material_name ?? "").Trim().StartsWith("Màng cán", StringComparison.OrdinalIgnoreCase)))
            {
                bom.material_id = laminationMaterial?.material_id;
                bom.material_code = EstimateHelper.Trunc20(laminationSnapshot?.code ?? "LAMINATION");
                bom.material_name = laminationSnapshot?.name ?? "Màng cán";
                bom.unit = laminationSnapshot?.unit ?? "kg";
            }
            foreach (var bom in allBoms.Where(x =>
                         waveCodesToMatch.Contains((x.material_code ?? "").Trim()) ||
                         (x.material_name ?? "").Trim().StartsWith("Sóng ", StringComparison.OrdinalIgnoreCase)))
            {
                bom.material_id = waveMaterial?.material_id;
                bom.material_code = EstimateHelper.Trunc20(resolvedWaveType ?? "");
                bom.material_name = resolvedWaveName ?? (string.IsNullOrWhiteSpace(resolvedWaveType)
                    ? "Sóng"
                    : $"Sóng {resolvedWaveType}");
            }

            var mountingGlueCode = string.IsNullOrWhiteSpace(resolvedWaveType)
                ? "MOUNTING_GLUE"
                : $"BOI_{resolvedWaveType}";

            foreach (var bom in allBoms.Where(x =>
                         string.Equals((x.material_name ?? "").Trim(), "Keo bồi", StringComparison.OrdinalIgnoreCase) ||
                         ((x.material_code ?? "").Trim().StartsWith("BOI_", StringComparison.OrdinalIgnoreCase)) ||
                         string.Equals((x.material_code ?? "").Trim(), "MOUNTING_GLUE", StringComparison.OrdinalIgnoreCase)))
            {
                bom.material_code = EstimateHelper.Trunc20(mountingGlueCode);
                bom.material_name = "Keo bồi";
            }
        }

        public async Task<GenerateConsultantContractResponse> GenerateConsultantContractAsync(
    int requestId,
    int estimateId,
    CancellationToken ct = default)
        {
            if (requestId <= 0)
                throw new ArgumentException("request_id must be > 0");

            if (estimateId <= 0)
                throw new ArgumentException("estimate_id must be > 0");

            var request = await _requestRepository.GetByIdAsync(requestId);
            if (request == null)
                throw new InvalidOperationException("Order request not found");

            var estimate = await _estimateRepo.GetTrackingByIdAsync(estimateId, ct);
            if (estimate == null || estimate.order_request_id != requestId)
                throw new InvalidOperationException("Estimate not found");

            var vatPercent = await _requestRepository.GetVatPercentAsync(ct);

            var templatePath = Path.Combine(
                AppContext.BaseDirectory,
                "Templates",
                "HopDongTemplate.docx");

            if (!File.Exists(templatePath))
                throw new FileNotFoundException("Contract template not found", templatePath);

            var signDate = AppTime.NowVnUnspecified();

            var placeholders = ContractDocxHelper.BuildPlaceholders(
                request,
                estimate,
                vatPercent,
                signDate);

            var templateBytes = await File.ReadAllBytesAsync(templatePath, ct);
            var generatedBytes = ContractDocxHelper.GenerateDocx(templateBytes, placeholders);

            await using var docxStream = new MemoryStream(generatedBytes);

            var safeFileName = $"contract_request_{requestId}_estimate_{estimateId}.docx";
            var publicId = $"contracts/request_{requestId}/estimate_{estimateId}/contract";

            var url = await _cloudinaryStorage.UploadRawWithPublicIdAsync(
                docxStream,
                safeFileName,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                publicId);

            estimate.consultant_contract_path = url;
            await _estimateRepo.SaveChangesAsync();

            var amounts = ContractDocxHelper.CalculateAmounts(estimate, vatPercent);

            return new GenerateConsultantContractResponse
            {
                request_id = requestId,
                estimate_id = estimateId,
                vat_percent = amounts.VatPercent,
                subtotal_before_vat = amounts.SubtotalBeforeVat,
                vat_amount = amounts.VatAmount,
                final_total_cost = amounts.FinalTotalCost,
                deposit_amount = amounts.DepositAmount,
                remaining_amount = amounts.RemainingAmount,
                consultant_contract_path = url,
                message = "Generate consultant contract successfully"
            };
        }

        private static string NormEstimateMaterialCode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            var s = value.Trim().ToUpperInvariant();

            s = s.Replace("Đ", "D");
            s = s.Replace(" ", "_");
            s = s.Replace("-", "_");

            while (s.Contains("__"))
                s = s.Replace("__", "_");

            return s.Trim('_');
        }

        private static string? ResolveCoatingMaterialCode(string? coatingMaterialCode, string? coatingType)
        {
            if (!string.IsNullOrWhiteSpace(coatingMaterialCode))
                return NormEstimateMaterialCode(coatingMaterialCode);

            var raw = NormEstimateMaterialCode(coatingType);

            return raw switch
            {
                "KEO_NUOC" or "KEO_PHU_NUOC" or "KEO_PHU_NUOC_" => "KEO_PHU_NUOC",

                "KEO_DAU" or "KEO_PHU_DAU" => "KEO_PHU_DAU",

                "UV" or "KEO_UV" or "PHU_UV" or "KEO_PHU_UV" => "KEO_PHU_UV",

                _ => string.IsNullOrWhiteSpace(raw) ? null : raw
            };
        }

        private static bool HasEstimateProcess(string? csv, string code)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return false;

            var target = NormEstimateMaterialCode(code);

            return csv
                .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormEstimateMaterialCode)
                .Any(x => string.Equals(x, target, StringComparison.OrdinalIgnoreCase));
        }

        public async Task SaveConsultantContractPathAsync(int requestId, int estimateId, string consultantContractPath, CancellationToken ct = default)
        {
            if (requestId <= 0)
                throw new ArgumentException("request_id must be > 0");

            if (estimateId <= 0)
                throw new ArgumentException("estimate_id must be > 0");

            if (string.IsNullOrWhiteSpace(consultantContractPath))
                throw new ArgumentException("consultant_contract_path is required");

            var estimate = await _estimateRepo.GetTrackingByIdAsync(estimateId, ct);
            if (estimate == null || estimate.order_request_id != requestId)
                throw new InvalidOperationException("Estimate not found");

            estimate.consultant_contract_path = consultantContractPath.Trim();

            await _estimateRepo.SaveChangesAsync();
        }
    }
}