using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;
using AMMS.Shared.DTOs.Requests;
using AMMS.Shared.DTOs.Socket;
using AMMS.Shared.DTOs.User;
using AMMS.Shared.Helpers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AMMS.Application.Services
{
    public class RequestService : IRequestService
    {
        private readonly NotificationService _notificationService;
        private readonly IRequestRepository _requestRepo;
        private readonly IOrderRepository _orderRepo;
        private readonly AppDbContext _db;
        private readonly ICostEstimateRepository _estimateRepo;
        private readonly IMaterialRepository _materialRepo;
        private readonly IBomRepository _bomRepo;
        private readonly IRealtimePublisher _rt;
        private readonly IHubContext<RealtimeHub> _hub;
        private readonly IAccessService _currentUser;
        private readonly IUserRepository _userRepo;
        private readonly ICloudinaryFileStorageService _cloudinaryStorage;
        private readonly IAccessService _accessService;
        private readonly IProductTypeRepository _productTypeRepo;
        private readonly IProductTypeProcessRepository _productTypeProcessRepo;
        private readonly IProductionSchedulingService _productionSchedulingService;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<RequestService> _logger;

        public RequestService(
            NotificationService notificationService,
            IRequestRepository requestRepo,
            IOrderRepository orderRepo,
            ICostEstimateRepository estimateRepo,
            IMaterialRepository materialRepo,
            IBomRepository bomRepo,
            IRealtimePublisher rt,
            IHubContext<RealtimeHub> hub,
            AppDbContext db,
            IAccessService currentUser,
            IUserRepository userRepo,
            ICloudinaryFileStorageService cloudinaryStorage,
            IAccessService accessService,
            IProductTypeRepository productTypeRepo,
            IProductTypeProcessRepository productTypeProcessRepo,
            IProductionSchedulingService productionSchedulingService,
            IServiceScopeFactory serviceScopeFactory,
            ILogger<RequestService> logger)
        {
            _notificationService = notificationService;
            _requestRepo = requestRepo;
            _orderRepo = orderRepo;
            _estimateRepo = estimateRepo;
            _materialRepo = materialRepo;
            _bomRepo = bomRepo;
            _db = db;
            _rt = rt;
            _hub = hub;
            _currentUser = currentUser;
            _userRepo = userRepo;
            _cloudinaryStorage = cloudinaryStorage;
            _accessService = accessService;
            _productTypeRepo = productTypeRepo;
            _productTypeProcessRepo = productTypeProcessRepo;
            _productionSchedulingService = productionSchedulingService;
            _scopeFactory = serviceScopeFactory;
            _logger = logger;
        }

        private DateTime? ToDeliveryDate(DateTime? dateTime)
        {
            return AppTime.NormalizeToVnDateOnlyUnspecified(dateTime);
        }

        private static string Trunc20(string? s)
        {
            s = (s ?? "").Trim();
            if (s.Length <= 20) return s;
            return s.Substring(0, 20);
        }

        public async Task<CreateRequestResponse> CreateAsync(CreateResquest req)
        {
            var now = AppTime.NowVnUnspecified();

            var assignedConsultant = await ResolveAssignedConsultantAsync();

            var entity = new order_request
            {
                customer_name = req.customer_name,
                customer_phone = req.customer_phone,
                customer_email = req.customer_email,
                delivery_date = ToDeliveryDate(req.delivery_date),
                delivery_date_change_reason = req.delivery_date_change_reason,
                product_name = req.product_name,
                quantity = req.quantity,
                description = req.description,
                design_file_path = req.design_file_path,
                order_request_date = now,
                detail_address = req.detail_address,
                process_status = "Pending",
                is_send_design = req.is_send_design,
                product_height_mm = req.product_height_mm,
                product_length_mm = req.product_length_mm,
                product_width_mm = req.product_width_mm,
                preliminary_estimated_price = req.preliminary_estimated_price,
                estimate_finish_date = now.AddDays(7),
                assigned_consultant = assignedConsultant.user_id,
                assigned_at = now,
                actual_consultant_user_id = null
            };
            ApplyAssignedConsultant(entity, assignedConsultant, now);
            await _requestRepo.AddAsync(entity);
            await _requestRepo.SaveChangesAsync();

            await _hub.Clients.Group(RealtimeGroups.ByRole("consultant"))
                .SendAsync("pending", new
                {
                    message = $"Có yêu cầu #{entity.order_request_id} mới được tạo",
                    id = entity.order_request_id
                });
            await _notificationService.CreateNotfi(2, $"Có yêu cầu #{entity.order_request_id} mới được tạo", entity.assigned_consultant, entity.order_request_id, "Pending");

            //khánh sửa signalr
            await _hub.Clients.Group(RealtimeGroups.ByRole("consultant")).SendAsync("pending", new { message = $"Có yêu cầu #{entity.order_request_id} mới được tạo", user_id = entity.assigned_consultant });

            return new CreateRequestResponse
            {
                order_request_id = entity.order_request_id,
                assigned_consultant = entity.assigned_consultant,
                assigned_at = entity.assigned_at,
                assign_name = entity.assign_name,
                assigned_consultant_user = assignedConsultant
            };
        }

        public async Task<CreateRequestResponse> CreateRequestByConsultantAsync(CreateResquestConsultant req)
        {
            var now = AppTime.NowVnUnspecified();

            var assignedConsultant = await ResolveAssignedConsultantAsync();

            var entity = new order_request
            {
                customer_name = req.customer_name,
                customer_phone = req.customer_phone,
                customer_email = req.customer_email,
                detail_address = req.detail_address,
                order_request_date = now,
                process_status = "Pending",
                assigned_consultant = assignedConsultant.user_id,
                assigned_at = now,
                actual_consultant_user_id = GetActualConsultantUserId()
            };
            ApplyAssignedConsultant(entity, assignedConsultant, now);

            await _requestRepo.AddAsync(entity);
            await _requestRepo.SaveChangesAsync();

            await _hub.Clients.Group(RealtimeGroups.ByRole("manager"))
                .SendAsync("consultantCreateRequest", new
                {
                    message = $"Có yêu cầu {entity.order_request_id} cần duyệt",
                    id = entity.order_request_id
                });

            await _notificationService.CreateNotfi(3, $"Có yêu cầu {entity.order_request_id} cần duyệt", null, entity.order_request_id, "Processing");
            //Khánh sửa signalr
            await _hub.Clients.Group(RealtimeGroups.ByRole("manager")).SendAsync("consultantCreateRequest", new { message = $"Có yêu cầu {entity.order_request_id} cần duyệt", id = entity.order_request_id });

            await _notificationService.CreateNotfi(3, $"Có yêu cầu {entity.order_request_id} vừa được tạo", null, entity.order_request_id, "Pending");
            return new CreateRequestResponse
            {
                order_request_id = entity.order_request_id,
                assigned_consultant = entity.assigned_consultant,
                assign_name = entity.assign_name,
                assigned_at = entity.assigned_at,
                assigned_consultant_user = assignedConsultant
            };
        }

        public async Task<UpdateRequestResponse> UpdateAsync(int id, UpdateOrderRequest req)
        {
            var entity = await _requestRepo.GetByIdAsync(id);
            if (entity == null)
            {
                return new UpdateRequestResponse
                {
                    Success = false,
                    Message = "Order request not found",
                    UpdatedId = id
                };
            }

            var ce = await _estimateRepo.GetByOrderRequestIdAsync(id);
            var oldDeliveryDate = entity.delivery_date;
            var newDeliveryDate = ToDeliveryDate(req.delivery_date);

            entity.customer_name = req.customer_name ?? entity.customer_name;
            entity.customer_phone = req.customer_phone ?? entity.customer_phone;
            entity.customer_email = req.customer_email ?? entity.customer_email;
            entity.product_name = req.product_name ?? entity.product_name;
            entity.quantity = req.quantity ?? entity.quantity;
            entity.description = req.description ?? entity.description;
            entity.design_file_path = req.design_file_path ?? entity.design_file_path;
            entity.order_request_date = req.order_request_date ?? entity.order_request_date;
            entity.detail_address = req.detail_address ?? entity.detail_address;
            entity.product_type = req.product_type ?? entity.product_type;
            entity.number_of_plates = req.number_of_plates ?? entity.number_of_plates;
            entity.product_length_mm = req.product_length_mm ?? entity.product_length_mm;
            entity.product_width_mm = req.product_width_mm ?? entity.product_width_mm;
            entity.product_height_mm = req.product_height_mm ?? entity.product_height_mm;
            entity.glue_tab_mm = req.glue_tab_mm ?? entity.glue_tab_mm;
            entity.bleed_mm = req.bleed_mm ?? entity.bleed_mm;
            entity.is_one_side_box = req.is_one_side_box ?? entity.is_one_side_box;
            entity.print_width_mm = req.print_width_mm ?? entity.print_width_mm;
            entity.print_length_mm = req.print_length_mm ?? entity.print_length_mm;
            entity.is_send_design = req.is_send_design ?? entity.is_send_design;
            entity.preliminary_estimated_price = req.preliminary_estimated_price ?? entity.preliminary_estimated_price;
            entity.reason = req.reason ?? entity.reason;
            entity.note = req.note ?? entity.note;
            entity.consultant_note = req.consultant_note ?? entity.consultant_note;
            entity.message_to_customer = req.message_to_customer ?? entity.message_to_customer;
            entity.delivery_note = req.delivery_note ?? entity.delivery_note;
            entity.print_ready_file = req.print_ready_file ?? entity.print_ready_file;

            entity.delivery_date = ToDeliveryDate(req.delivery_date);

            if (req.delivery_date.HasValue)
            {
                entity.delivery_date = newDeliveryDate;

                var changed = oldDeliveryDate != newDeliveryDate;
                if (changed && req.delivery_date_change_reason != null)
                {
                    entity.delivery_date_change_reason = string.IsNullOrWhiteSpace(req.delivery_date_change_reason)
                        ? null
                        : req.delivery_date_change_reason.Trim();
                }
            }
            else if (req.delivery_date_change_reason != null)
            {
                entity.delivery_date_change_reason = string.IsNullOrWhiteSpace(req.delivery_date_change_reason)
                    ? null
                    : req.delivery_date_change_reason.Trim();
            }

            // ===== cost_estimate =====
            if (ce != null)
            {
                if (!string.IsNullOrWhiteSpace(req.production_processes))
                    ce.production_processes = req.production_processes.Trim();

                if (!string.IsNullOrWhiteSpace(req.paper_code))
                    ce.paper_code = req.paper_code.Trim();

                if (!string.IsNullOrWhiteSpace(req.paper_name))
                    ce.paper_name = req.paper_name.Trim();

                if (!string.IsNullOrWhiteSpace(req.wave_type))
                    ce.wave_type = req.wave_type.Trim();

                if (!string.IsNullOrWhiteSpace(req.coating_type))
                    ce.coating_type = req.coating_type.Trim();

                if (req.paper_alternative != null)
                    ce.paper_alternative = string.IsNullOrWhiteSpace(req.paper_alternative)
                        ? null
                        : req.paper_alternative.Trim();

                if (req.wave_alternative != null)
                    ce.wave_alternative = string.IsNullOrWhiteSpace(req.wave_alternative)
                        ? null
                        : req.wave_alternative.Trim();

                if (req.cost_note != null)
                    ce.cost_note = string.IsNullOrWhiteSpace(req.cost_note)
                        ? null
                        : req.cost_note.Trim();

                if (req.ink_type_names != null)
                    ce.ink_type_names = string.IsNullOrWhiteSpace(req.ink_type_names)
                        ? null
                        : req.ink_type_names.Trim();

                if (req.alternative_material_reason != null)
                    ce.alternative_material_reason = string.IsNullOrWhiteSpace(req.alternative_material_reason)
                        ? null
                        : req.alternative_material_reason.Trim();
            }

            entity.process_status = "Pending";
            entity.verified_at = null;
            entity.quote_expires_at = null;
            entity.accepted_estimate_id = null;

            StampActualConsultant(entity);

            await _requestRepo.UpdateAsync(entity);
            await _requestRepo.SaveChangesAsync();

            return new UpdateRequestResponse
            {
                Success = true,
                Message = "Order request updated successfully",
                UpdatedId = id,
                UpdatedAt = AppTime.NowVnUnspecified()
            };
        }

        public async Task CancelAsync(int id, string? reason, CancellationToken ct = default)
        {

            var entity = await _requestRepo.GetByIdAsync(id);
            if (entity == null) return;

            if (entity.order_id != null)
                throw new InvalidOperationException("This request is already linked to an order, cannot cancel.");

            entity.reason = reason;
            entity.process_status = "Cancel";
            StampActualConsultant(entity);

            await _requestRepo.UpdateAsync(entity);
            await _requestRepo.SaveChangesAsync();
        }

        public Task<order_request?> GetByIdAsync(int id) => _requestRepo.GetByIdAsync(id);

        public async Task<RequestWithCostDto?> GetByIdWithCostAsync(int id)
        {
            var consultantUserId = await _currentUser.GetConsultantScopeUserIdAsync();
            return await _requestRepo.GetByIdWithCostAsync(id, consultantUserId);
        }

        public async Task<PagedResultLite<RequestPagedDto>> GetPagedAsync(int page, int pageSize)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;
            var consultantUserId = await _currentUser.GetConsultantScopeUserIdAsync();

            var list = await _requestRepo.GetPagedAsync(skip, pageSize + 1, consultantUserId);

            var hasNext = list.Count > pageSize;
            var data = hasNext ? list.Take(pageSize).ToList() : list;

            return new PagedResultLite<RequestPagedDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = data
            };
        }

        public async Task<RequestPagedDto?> GetByOrderIdAsync(int orderId)
        {
            return await _requestRepo.GetByOrderIdAsync(orderId);
        }

        public async Task<ConvertRequestToOrderResponse> ConvertToOrderAsync(int requestId)
        {
            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync();

                try
                {
                    var result = await ConvertToOrderInternalAsync(requestId);

                    if (!result.Success)
                    {
                        await tx.RollbackAsync();
                        return result;
                    }

                    await tx.CommitAsync();
                    return result;
                }
                catch
                {
                    await tx.RollbackAsync();
                    throw;
                }
            });
        }
        public async Task<RequestDetailDto?> GetInformationRequestById(int requestId, CancellationToken ct = default)
        {
            var consultantUserId = await _currentUser.GetConsultantScopeUserIdAsync(ct);
            return await _requestRepo.GetInformationRequestById(requestId, consultantUserId, ct);
        }

        public Task<PagedResultLite<RequestEmailStatsDto>> GetEmailsByAcceptedCountPagedAsync(
            int page, int pageSize, CancellationToken ct = default)
            => _requestRepo.GetEmailsByAcceptedCountPagedAsync(page, pageSize, ct);

        public async Task<PagedResultLite<RequestStockCoverageDto>> GetSortedByStockCoveragePagedAsync(
    int page, int pageSize, CancellationToken ct = default)
        {
            var consultantUserId = await _currentUser.GetConsultantScopeUserIdAsync(ct);
            return await _requestRepo.GetSortedByStockCoveragePagedAsync(page, pageSize, consultantUserId, ct);
        }

        public async Task<int> CreateOrderRequestAsync(CreateOrderRequestDto dto, CancellationToken ct = default)
        {
            if (dto.quantity is null or <= 0)
                throw new ArgumentException("quantity must be > 0");
            if (string.IsNullOrWhiteSpace(dto.product_name))
                throw new ArgumentException("product_name is required");

            var now = AppTime.NowVnUnspecified();
            var assignedConsultant = await ResolveAssignedConsultantAsync(ct);

            var entity = new order_request
            {
                customer_name = dto.customer_name?.Trim(),
                customer_phone = dto.customer_phone?.Trim(),
                customer_email = dto.customer_email?.Trim(),
                delivery_date = ToDeliveryDate(dto.delivery_date),
                detail_address = dto.detail_address?.Trim(),
                product_name = dto.product_name?.Trim(),
                quantity = dto.quantity,
                description = dto.description,
                design_file_path = dto.design_file_path,
                is_send_design = dto.is_send_design,
                product_type = dto.product_type?.Trim(),
                number_of_plates = dto.number_of_plates,
                product_length_mm = dto.product_length_mm,
                product_width_mm = dto.product_width_mm,
                product_height_mm = dto.product_height_mm,
                glue_tab_mm = dto.glue_tab_mm,
                bleed_mm = dto.bleed_mm,
                is_one_side_box = dto.is_one_side_box,
                print_width_mm = dto.print_width_mm,
                print_length_mm = dto.print_length_mm,
                order_request_date = now,
                process_status = "Pending",
                assigned_consultant = assignedConsultant.user_id,
                assigned_at = now,
            };
            ApplyAssignedConsultant(entity, assignedConsultant, now);
            await _requestRepo.AddAsync(entity);
            await _requestRepo.SaveChangesAsync();

            return entity.order_request_id;
        }

        public async Task<OrderRequestDesignFileResponse?> GetDesignFileAsync(int orderRequestId, CancellationToken ct = default)
        {
            var consultantUserId = await _currentUser.GetConsultantScopeUserIdAsync(ct);
            var path = await _requestRepo.GetDesignFilePathAsync(orderRequestId, consultantUserId, ct);

            if (path == null)
                return null;

            return new OrderRequestDesignFileResponse
            {
                order_request_id = orderRequestId,
                design_file_path = path
            };
        }

        public async Task UpdateDesignFilePathAsync(int orderRequestId, string designFilePath, CancellationToken ct = default)
        {
            var entity = await _requestRepo.GetByIdAsync(orderRequestId);
            if (entity == null)
                throw new Exception("Order request not found");

            entity.design_file_path = designFilePath;
            entity.is_send_design = true;

            await _requestRepo.UpdateAsync(entity);
            await _requestRepo.SaveChangesAsync();
        }
        public async Task<int> DeleteDesignFilePathByRequestIdAsync(int orderRequestId, CancellationToken ct = default)
        {
            return await _requestRepo.DeleteDesignFilePathByRequestIdAsync(orderRequestId, ct);
        }

        public async Task UpdateApprovalAsync(RequestApprovalUpdateDto dto, CancellationToken ct = default)
        {
            if (dto.request_id <= 0) throw new ArgumentException("request_id is required");

            var req = await _requestRepo.GetByIdAsync(dto.request_id);
            if (req == null) throw new InvalidOperationException("Order request not found");

            var st = (dto.status ?? "").Trim();

            st = st.Equals("verified", StringComparison.OrdinalIgnoreCase) ? "Verified" :
                 st.Equals("processing", StringComparison.OrdinalIgnoreCase) ? "Processing" :
                 st.Equals("declined", StringComparison.OrdinalIgnoreCase) ? "Declined" :
                 st;

            if (st is not ("Processing" or "Verified" or "Declined"))
                throw new ArgumentException("status must be Processing | Verified | Declined");

            if ((st == "Verified" || st == "Declined") &&
                !string.Equals(req.process_status, "Processing", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Request must be Processing before manager decision");
            }

            var now = AppTime.NowVnUnspecified();

            req.process_status = st;

            if (dto.note != null)
                req.note = dto.note;

            if (st == "Verified")
            {
                req.verified_at = now;
                req.quote_expires_at = now.AddDays(7);

                await _hub.Clients.Group(RealtimeGroups.ByRole("consultant"))
                    .SendAsync("verified", new
                    {
                        message = $"Yêu cầu #{req.order_request_id} đã được duyệt",
                        id = req.order_request_id
                    });
                await _notificationService.CreateNotfi(2, $"Yêu cầu #{req.order_request_id} đã được duyệt", req.assigned_consultant, req.order_request_id, "Verified");
                await _hub.Clients.Group(RealtimeGroups.ByRole("consultant")).SendAsync("verified", new { message = $"Yêu cầu #{req.order_request_id} đã được duyệt", user_id = req.assigned_consultant });
            }
            else
            {
                req.verified_at = null;
                req.quote_expires_at = null;
            }

            if (st == "Declined")
            {
                req.accepted_estimate_id = null;
                await _estimateRepo.DeactivateAllByRequestIdAsync(dto.request_id, ct);

                await _hub.Clients.Group(RealtimeGroups.ByRole("consultant"))
                    .SendAsync("declined", new
                    {
                        message = $"Yêu cầu #{req.order_request_id} chưa được duyệt, cần chỉnh sửa",
                        id = req.order_request_id
                    });
                await _notificationService.CreateNotfi(2, $"Yêu cầu #{req.order_request_id} chưa được duyệt, cần chỉnh sửa", req.assigned_consultant, req.order_request_id, "Declined");
                await _hub.Clients.Group(RealtimeGroups.ByRole("consultant")).SendAsync("declined", new { message = $"Yêu cầu #{req.order_request_id} chưa được duyệt, cần chỉnh sửa", user_id = req.assigned_consultant });
            }
            await _hub.Clients.All.SendAsync("update-ui", new { message = "update UI" });
            await _requestRepo.SaveChangesAsync();
        }

        public async Task SubmitEstimateForApprovalAsync(SubmitForApprovalRequestDto input)
        {
            if (input.request_id <= 0)
                throw new ArgumentException("request_id is required");

            var req = await _requestRepo.GetByIdAsync(input.request_id);
            if (req == null)
                throw new InvalidOperationException("Order request not found");

            var st = (req.process_status ?? "").Trim();
            if (st.Equals("Accepted", StringComparison.OrdinalIgnoreCase) ||
                st.Equals("Rejected", StringComparison.OrdinalIgnoreCase) ||
                st.Equals("Cancel", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Cannot submit when process_status = {req.process_status}");
            }

            var keepIds = await _db.cost_estimates
                .AsNoTracking()
                .Where(x => x.order_request_id == input.request_id && x.is_active)
                .OrderByDescending(x => x.estimate_id)
                .Select(x => x.estimate_id)
                .Take(2)
                .ToListAsync();

            if (keepIds.Count == 0)
                throw new InvalidOperationException("No active estimate found. Please create or revise estimate before submit.");

            await _db.cost_estimates
                .Where(x => x.order_request_id == input.request_id)
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(x => x.is_active, x => keepIds.Contains(x.estimate_id)));

            var normalizedNote = string.IsNullOrWhiteSpace(input.consultant_note)
                ? null
                : input.consultant_note.Trim();

            req.consultant_note = normalizedNote;
            req.process_status = "Processing";
            req.verified_at = null;
            req.quote_expires_at = null;
            req.accepted_estimate_id = null;

            StampActualConsultant(req);

            await _requestRepo.SaveChangesAsync();

            //Khánh sửa signalr
            await _hub.Clients.Group(RealtimeGroups.ByRole("manager")).SendAsync("processing", new { message = $"Có yêu cầu #{req.order_request_id} cần duyệt" });
            await _hub.Clients.All.SendAsync("update-ui", new { message = "update UI" });
            await _notificationService.CreateNotfi(3, $"Có yêu cầu #{req.order_request_id} cần duyệt", null, req.order_request_id, "Processing");
        }

        public async Task<RequestWithTwoEstimatesDto?> GetCompareQuotesAsync(int requestId, CancellationToken ct = default)
        {
            if (requestId <= 0) return null;

            var consultantUserId = await _currentUser.GetConsultantScopeUserIdAsync(ct);

            return await _requestRepo.GetActiveEstimatesInProcessAsync(requestId, consultantUserId, ct);
        }

        public async Task<CloneRequestResponseDto> CloneRequestAsync(int requestId, CancellationToken ct = default)
        {
            if (requestId <= 0)
                throw new ArgumentException("request_id is required");

            var source = await _requestRepo.GetByIdAsync(requestId);
            if (source == null)
                throw new InvalidOperationException("Order request not found");

            var consultantUserId = await _currentUser.GetConsultantScopeUserIdAsync(ct);
            var activeEstimates = await _requestRepo.GetActiveEstimatesWithProcessesByRequestIdAsync(requestId, consultantUserId, ct);

            var now = AppTime.NowVnUnspecified();

            var clonedAssignedConsultantId = source.assigned_consultant ?? await _requestRepo.GetLeastLoadedConsultantUserIdAsync(ct);

            AssignedConsultantSummaryDto? clonedAssignedConsultant = null;
            if (clonedAssignedConsultantId.HasValue)
            {
                clonedAssignedConsultant = await _userRepo.GetAssignedConsultantSummaryAsync(clonedAssignedConsultantId.Value, ct);
            }

            var clonedRequest = new order_request
            {
                customer_name = source.customer_name,
                customer_phone = source.customer_phone,
                customer_email = source.customer_email,
                delivery_date = source.delivery_date,
                product_name = source.product_name,
                quantity = source.quantity,
                description = source.description,
                design_file_path = source.design_file_path,
                order_request_date = now,
                detail_address = source.detail_address,
                process_status = "Pending",
                product_type = source.product_type,
                number_of_plates = source.number_of_plates,
                order_id = null,
                quote_id = null,
                accepted_estimate_id = null,
                product_length_mm = source.product_length_mm,
                product_width_mm = source.product_width_mm,
                product_height_mm = source.product_height_mm,
                glue_tab_mm = source.glue_tab_mm,
                bleed_mm = source.bleed_mm,
                is_one_side_box = source.is_one_side_box,
                print_width_mm = source.print_width_mm,
                print_length_mm = source.print_length_mm,
                is_send_design = source.is_send_design,
                note = null,
                reason = null,
                verified_at = null,
                quote_expires_at = null,
                consultant_note = source.consultant_note,
                assigned_consultant = clonedAssignedConsultantId,
                assigned_at = clonedAssignedConsultantId.HasValue ? now : null,
                actual_consultant_user_id = GetActualConsultantUserId(),
                assign_name = ResolveAssignName(clonedAssignedConsultant),
            };

            await _requestRepo.AddAsync(clonedRequest);
            await _requestRepo.SaveChangesAsync();

            var clonedEstimateIds = new List<int>();
            var clonedEstimateMap = new Dictionary<int, cost_estimate>();

            foreach (var est in activeEstimates.OrderBy(x => x.estimate_id))
            {
                var clonedEstimate = new cost_estimate
                {
                    order_request_id = clonedRequest.order_request_id,
                    previous_estimate_id = null,
                    paper_cost = est.paper_cost,
                    paper_sheets_used = est.paper_sheets_used,
                    paper_unit_price = est.paper_unit_price,
                    ink_cost = est.ink_cost,
                    ink_weight_kg = est.ink_weight_kg,
                    ink_rate_per_m2 = est.ink_rate_per_m2,
                    coating_glue_cost = est.coating_glue_cost,
                    coating_glue_weight_kg = est.coating_glue_weight_kg,
                    coating_glue_rate_per_m2 = est.coating_glue_rate_per_m2,
                    coating_type = est.coating_type,
                    mounting_glue_cost = est.mounting_glue_cost,
                    mounting_glue_weight_kg = est.mounting_glue_weight_kg,
                    mounting_glue_rate_per_m2 = est.mounting_glue_rate_per_m2,
                    lamination_cost = est.lamination_cost,
                    lamination_weight_kg = est.lamination_weight_kg,
                    lamination_rate_per_m2 = est.lamination_rate_per_m2,
                    material_cost = est.material_cost,
                    base_cost = est.base_cost,
                    is_rush = est.is_rush,
                    rush_percent = est.rush_percent,
                    rush_amount = est.rush_amount,
                    days_early = est.days_early,
                    subtotal = est.subtotal,
                    discount_percent = est.discount_percent,
                    discount_amount = est.discount_amount,
                    final_total_cost = est.final_total_cost,
                    estimated_finish_date = est.estimated_finish_date,
                    desired_delivery_date = est.desired_delivery_date,
                    created_at = now,
                    sheets_required = est.sheets_required,
                    sheets_waste = est.sheets_waste,
                    sheets_total = est.sheets_total,
                    n_up = est.n_up,
                    total_area_m2 = est.total_area_m2,
                    design_cost = est.design_cost,
                    cost_note = est.cost_note,
                    is_active = true,
                    paper_code = est.paper_code,
                    paper_name = est.paper_name,
                    wave_type = est.wave_type,
                    wave_sheets_used = est.wave_sheets_used,
                    paper_alternative = est.paper_alternative,
                    wave_alternative = est.wave_alternative,
                    production_processes = est.production_processes,
                    waste_gluing_boxes = est.waste_gluing_boxes,
                    sheet_area_m2 = est.sheet_area_m2,
                    print_sheets_used = est.print_sheets_used,
                    total_coating_area_m2 = est.total_coating_area_m2,
                    total_lamination_area_m2 = est.total_lamination_area_m2,
                    coating_sheets_used = est.coating_sheets_used,
                    lamination_sheets_used = est.lamination_sheets_used,
                    wave_sheet_area_m2 = est.wave_sheet_area_m2,
                    wave_n_up = est.wave_n_up,
                    wave_sheets_required = est.wave_sheets_required,
                    total_mounting_area_m2 = est.total_mounting_area_m2,
                    wave_unit_price = est.wave_unit_price,
                    wave_cost = est.wave_cost,
                    total_process_cost = est.total_process_cost,
                    lamination_material_id = est.lamination_material_id,
                    lamination_material_code = est.lamination_material_code,
                    lamination_material_name = est.lamination_material_name,
                };

                if (est.process_costs != null && est.process_costs.Count > 0)
                {
                    foreach (var pc in est.process_costs)
                    {
                        clonedEstimate.process_costs.Add(new cost_estimate_process
                        {
                            process_code = pc.process_code,
                            process_name = pc.process_name,
                            quantity = pc.quantity,
                            unit = pc.unit,
                            unit_price = pc.unit_price,
                            total_cost = pc.total_cost,
                            note = pc.note,
                            created_at = now
                        });
                    }
                }

                await _estimateRepo.AddAsync(clonedEstimate);
                await _estimateRepo.SaveChangesAsync();

                clonedEstimateIds.Add(clonedEstimate.estimate_id);
                clonedEstimateMap[est.estimate_id] = clonedEstimate;
            }

            foreach (var oldEst in activeEstimates)
            {
                if (!oldEst.previous_estimate_id.HasValue)
                    continue;

                if (!clonedEstimateMap.TryGetValue(oldEst.estimate_id, out var newEst))
                    continue;

                if (!clonedEstimateMap.TryGetValue(oldEst.previous_estimate_id.Value, out var newPrevEst))
                    continue;

                newEst.previous_estimate_id = newPrevEst.estimate_id;
            }

            await _estimateRepo.SaveChangesAsync();

            await _hub.Clients.Group(RealtimeGroups.ByRole("manager")).SendAsync("clone-request", new { message = $"Có yêu cầu {clonedRequest.order_request_id} vừa được tạo", user_id = clonedRequest.assigned_consultant });

            await _notificationService.CreateNotfi(3, $"Có yêu cầu {clonedRequest.order_request_id} vừa được tạo", null, clonedRequest.order_request_id, "Pending");

            await _hub.Clients.All.SendAsync("update-ui", new { message = "update UI" });

            return new CloneRequestResponseDto
            {
                source_request_id = requestId,
                cloned_request_id = clonedRequest.order_request_id,
                cloned_estimate_ids = clonedEstimateIds,
                message = "Cloned request successfully"
            };
        }

        public async Task UpdateConsultantMessageToCustomerAsync(int requestId, string? message, CancellationToken ct = default)
        {
            if (requestId <= 0)
                throw new ArgumentException("request_id is required");

            var req = await _requestRepo.GetByIdAsync(requestId);
            if (req == null)
                throw new InvalidOperationException("Order request not found");

            if (!string.Equals(req.process_status, "Verified", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only Verified request can update consultant message to customer");

            req.message_to_customer = string.IsNullOrWhiteSpace(message)
                ? null
                : message.Trim();

            StampActualConsultant(req);

            await _requestRepo.SaveChangesAsync();
        }

        private async Task<string> NormalizeProductionProcessCsvAsync(
    int productTypeId,
    string? rawCsv,
    CancellationToken ct = default)
        {
            var allSteps = await _productTypeProcessRepo.GetActiveByProductTypeIdAsync(productTypeId, ct);

            var selected = ProductionProcessSelectionHelper.ResolveFixedRoute(
                allSteps,
                x => x.process_code,
                rawCsv);

            return ProductionProcessSelectionHelper.BuildCsv(selected, x => x.process_code);
        }

        public async Task<order_request?> GetRequestForUpdateAsync(int orderRequestId, CancellationToken ct)
        {
            return await _requestRepo.GetRequestForUpdateAsync(orderRequestId, ct);
        }

        private async Task<ConvertRequestToOrderResponse> ConvertToOrderInternalAsync(int requestId)
        {
            var req = await _requestRepo.GetByIdAsync(requestId);
            if (req == null)
            {
                return new ConvertRequestToOrderResponse
                {
                    Success = false,
                    Message = "Order request not found",
                    RequestId = requestId
                };
            }

            if (!string.Equals(req.process_status?.Trim(), "Accepted", StringComparison.OrdinalIgnoreCase))
            {
                return new ConvertRequestToOrderResponse
                {
                    Success = false,
                    Message = "Only process_status = 'Accepted' can be converted to order",
                    RequestId = requestId
                };
            }

            if (req.quote_id == null)
            {
                return new ConvertRequestToOrderResponse
                {
                    Success = false,
                    Message = "No quote found for this request",
                    RequestId = requestId
                };
            }

            if (req.order_id != null || await _requestRepo.AnyOrderLinkedAsync(requestId))
            {
                return new ConvertRequestToOrderResponse
                {
                    Success = true,
                    Message = "This request was already converted",
                    RequestId = requestId,
                    OrderId = req.order_id
                };
            }

            var est = await ResolveEstimateForRequestAsync(req);
            if (est == null)
            {
                return new ConvertRequestToOrderResponse
                {
                    Success = false,
                    Message = "Cost estimate not found for this request",
                    RequestId = requestId
                };
            }

            var ptCode = (req.product_type ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ptCode))
            {
                return new ConvertRequestToOrderResponse
                {
                    Success = false,
                    Message = "order_request.product_type is missing, cannot map product_type_id",
                    RequestId = requestId
                };
            }

            var productTypeId = await _productTypeRepo.GetIdByCodeAsync(ptCode);
            if (!productTypeId.HasValue)
            {
                return new ConvertRequestToOrderResponse
                {
                    Success = false,
                    Message = $"Product type code '{ptCode}' not found in product_types",
                    RequestId = requestId
                };
            }

            var normalizedProcessCsv = await NormalizeProductionProcessCsvAsync(
                productTypeId.Value,
                est.production_processes);

            var newOrder = new order
            {
                code = "TMP-ORD",
                order_date = AppTime.NowVnUnspecified(),
                delivery_date = req.delivery_date,
                status = "LayoutPending",
                payment_status = "Deposited",
                quote_id = req.quote_id,
                total_amount = est.final_total_cost,
                is_enough = null,
                is_buy = false,
                layout_confirmed = false,
                is_production_ready = false
            };

            await _orderRepo.AddOrderAsync(newOrder);
            await _orderRepo.SaveChangesAsync();

            newOrder.code = $"ORD-{newOrder.order_id:00000}";
            _orderRepo.Update(newOrder);
            await _orderRepo.SaveChangesAsync();

            var resolvedPaperCode = EstimateMaterialAlternativeHelper.ResolvePaperCode(
                est.paper_alternative,
                est.paper_code);

            var resolvedWaveType = EstimateMaterialAlternativeHelper.ResolveWaveType(
                est.wave_alternative,
                est.wave_type);

            material? resolvedPaperMaterial = null;
            string? resolvedPaperName = est.paper_name;

            if (!string.IsNullOrWhiteSpace(resolvedPaperCode))
            {
                resolvedPaperMaterial = await _materialRepo.GetByCodeAsync(resolvedPaperCode);
                resolvedPaperName = resolvedPaperMaterial?.name ?? est.paper_name ?? resolvedPaperCode;
            }

            material? resolvedWaveMaterial = null;
            string? resolvedWaveName = null;

            if (!string.IsNullOrWhiteSpace(resolvedWaveType))
            {
                resolvedWaveMaterial = await _materialRepo.GetByCodeAsync(resolvedWaveType);
                resolvedWaveName = resolvedWaveMaterial?.name ?? $"Sóng {resolvedWaveType}";
            }

            var newItem = new order_item
            {
                order_id = newOrder.order_id,
                product_name = req.product_name,
                quantity = req.quantity ?? 0,
                design_url = req.design_file_path,
                product_type_id = productTypeId,

                paper_code = resolvedPaperCode,
                production_process = normalizedProcessCsv,
                paper_name = resolvedPaperName,
                glue_type = est.coating_type,
                wave_type = resolvedWaveType,

                est_paper_sheets_total = est.sheets_total,
                est_ink_weight_kg = est.ink_weight_kg,
                est_coating_glue_weight_kg = est.coating_glue_weight_kg,
                est_mounting_glue_weight_kg = est.mounting_glue_weight_kg,
                est_lamination_weight_kg = est.lamination_weight_kg,
                height_mm = req.product_height_mm,
                length_mm = req.product_length_mm,
                width_mm = req.product_width_mm
            };

            await _orderRepo.AddOrderItemAsync(newItem);
            await _orderRepo.SaveChangesAsync();

            var qty = (decimal)(newItem.quantity <= 0 ? 1 : newItem.quantity);

            var inkMaterial = await ResolveMaterialByCodesAsync("INK");

            var coatingMaterial = await ResolveMaterialByCodesAsync(
    est.coating_type,
    NormalizeMaterialAlias(est.coating_type),
    "KEO_PHU_NUOC",
    "KEO_PHU_DAU");

            var laminationSnapshot = await EstimateMaterialSnapshotHelper.ResolveAsync(
    _materialRepo,
    est.lamination_material_id,
    est.lamination_material_code,
    est.lamination_material_name,
    defaultName: "Màng cán",
    defaultUnit: "kg");

            var laminationMaterial = laminationSnapshot?.material;

            var mountingGlueMaterial = await ResolveMaterialByCodesAsync(
                "KEO_BOI",
                "MOUNTING_GLUE");

            var bomLines = new List<bom>();

            void AddBomLine(
    material? materialEntity,
    string fallbackCode,
    string fallbackName,
    string unit,
    decimal qtyTotal)
            {
                if (qtyTotal <= 0) return;

                if (materialEntity == null)
                {
                    throw new InvalidOperationException(
                        $"Không map được vật tư BOM. code={fallbackCode}, name={fallbackName}, qty={qtyTotal}");
                }

                var code = !string.IsNullOrWhiteSpace(materialEntity.code)
                    ? materialEntity.code
                    : fallbackCode;

                var name = !string.IsNullOrWhiteSpace(materialEntity.name)
                    ? materialEntity.name
                    : fallbackName;

                bomLines.Add(CreateBomLine(
                    orderItemId: newItem.item_id,
                    estimateId: est.estimate_id,
                    orderQty: qty,
                    materialId: materialEntity.material_id,
                    materialCode: Trunc20(code),
                    materialName: name,
                    unit: unit,
                    qtyTotal: qtyTotal));
            }

            AddBomLine(
                resolvedPaperMaterial,
                resolvedPaperCode ?? "PAPER",
                resolvedPaperName ?? "Giấy",
                "tờ",
                est.sheets_total);

            AddBomLine(
                resolvedWaveMaterial,
                resolvedWaveType ?? "WAVE",
                resolvedWaveName ?? "Sóng carton",
                "tờ",
                est.wave_sheets_used ?? 0);

            AddBomLine(
                inkMaterial,
                "INK",
                "Mực in",
                "kg",
                est.ink_weight_kg);

            AddBomLine(
                coatingMaterial,
                !string.IsNullOrWhiteSpace(est.coating_type) ? est.coating_type! : "COATING",
                ResolveCoatingMaterialName(est.coating_type),
                "kg",
                est.coating_glue_weight_kg);

            AddBomLine(
                mountingGlueMaterial,
                "MOUNTING_GLUE",
                "Keo bồi",
                "kg",
                est.mounting_glue_weight_kg);

            AddBomLine(
    laminationMaterial,
    laminationSnapshot?.code ?? "LAMINATION",
    laminationSnapshot?.name ?? "Màng cán",
    laminationSnapshot?.unit ?? "kg",
    est.lamination_weight_kg);

            foreach (var line in bomLines)
            {
                await _bomRepo.AddBomAsync(line);
            }

            await _bomRepo.SaveChangesAsync();

            newOrder.is_enough = await _orderRepo.IsOrderEnoughByOrderIdAsync(newOrder.order_id);
            _orderRepo.Update(newOrder);
            await _orderRepo.SaveChangesAsync();

            req.order_id = newOrder.order_id;
            await _requestRepo.UpdateAsync(req);
            await _requestRepo.SaveChangesAsync();

            return new ConvertRequestToOrderResponse
            {
                Success = true,
                Message = "Converted order request to order successfully",
                RequestId = requestId,
                OrderId = newOrder.order_id,
                OrderItemId = newItem.item_id,
                OrderCode = newOrder.code
            };
        }

        private void QueueConvertToOrder(int requestId)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();

                    var scopedRequestService = scope.ServiceProvider.GetRequiredService<IRequestService>();
                    var scopedRt = scope.ServiceProvider.GetRequiredService<IRealtimePublisher>();
                    var scopedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var result = await scopedRequestService.ConvertToOrderAsync(requestId);

                    if (!result.Success)
                    {
                        _logger.LogWarning(
                            "Background convert-to-order skipped/failed. RequestId={RequestId}, Message={Message}",
                            requestId, result.Message);
                        return;
                    }

                    var req = await scopedDb.order_requests
                        .AsNoTracking()
                        .FirstOrDefaultAsync(x => x.order_request_id == requestId);

                    //await _hub.Clients.Group(RealtimeGroups.ByRole("manager")).SendAsync("order-create-after-contract-approved", new { message = "Đã duyệt hợp đồng xong" });

                    await _hub.Clients.All.SendAsync("update-ui", new { message = "update UI" });

                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Background convert-to-order failed. RequestId={RequestId}",
                        requestId);
                }
            });
        }

        public Task<ConvertRequestToOrderResponse> ConvertToOrderInCurrentTransactionAsync(int requestId)
        {
            return ConvertToOrderInternalAsync(requestId);
        }

        public Task<int?> GetConsultantScopeUserIdAsync(CancellationToken ct = default) => _currentUser.GetConsultantScopeUserIdAsync(ct);

        public Task EnsureCanAccessAssignedRequestAsync(int requestId, CancellationToken ct = default)
            => _currentUser.EnsureCanAccessAssignedRequestAsync(requestId, ct);

        public async Task<bool> UpdateDeliveryNoteAsync(int orderId, string note, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(note))
                note = "";

            return await _requestRepo.UpdateDeliveryNoteAsync(orderId, note.Trim(), ct);
        }

        private async Task<AssignedConsultantSummaryDto> ResolveAssignedConsultantAsync(CancellationToken ct = default)
        {
            int? assignedConsultantId;

            if (_currentUser.IsConsultant && _currentUser.UserId.HasValue)
            {
                assignedConsultantId = _currentUser.UserId.Value;
            }
            else
            {
                assignedConsultantId = await _requestRepo.GetLeastLoadedConsultantUserIdAsync(ct);
            }

            if (!assignedConsultantId.HasValue)
                throw new InvalidOperationException("Hiện không có consultant nào đang hoạt động để nhận request.");

            var consultant = await _userRepo.GetAssignedConsultantSummaryAsync(assignedConsultantId.Value, ct);

            if (consultant == null)
                throw new InvalidOperationException("Consultant được gán không tồn tại.");

            if (consultant.role_id != 2)
                throw new InvalidOperationException("User được gán không phải consultant.");

            if (consultant.is_active != true)
                throw new InvalidOperationException("Consultant được gán hiện không hoạt động.");

            return consultant;
        }

        public async Task<DateTime?> CalculateAsync(int orderRequestId, CancellationToken ct = default)
        {
            return await _requestRepo.CalculateAsync(orderRequestId, ct);
        }
        public async Task<DateTime?> RecalculateAndPersistAsync(int orderRequestId, CancellationToken ct = default)
        {
            var preview = await _productionSchedulingService.PreviewByOrderRequestAsync(orderRequestId, ct);
            if (preview == null)
                return null;

            var tracked = await _requestRepo.GetRequestForUpdateAsync(orderRequestId, ct);
            if (tracked == null)
                return null;

            tracked.estimate_finish_date = DateTime.SpecifyKind(
                preview.estimated_finish_date,
                DateTimeKind.Unspecified);

            await _requestRepo.SaveChangesAsync();

            return tracked.estimate_finish_date;
        }

        public async Task<string> UploadPrintReadyFileAsync(
    int requestId,
    int? estimateId,
    Stream fileStream,
    string fileName,
    string? contentType,
    CancellationToken ct = default)
        {
            if (requestId <= 0)
                throw new ArgumentException("requestId must be > 0");

            if (fileStream == null)
                throw new ArgumentException("file is required");

            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("fileName is required");

            var request = await _requestRepo.GetByIdAsync(requestId);
            if (request == null)
                throw new InvalidOperationException("Order request not found");

            cost_estimate? selectedEstimate = null;

            if (estimateId.HasValue && estimateId.Value > 0)
            {
                selectedEstimate = await _estimateRepo.GetTrackingByIdAsync(
                    estimateId.Value,
                    ct);

                if (selectedEstimate == null || selectedEstimate.order_request_id != requestId)
                    throw new InvalidOperationException("Estimate not found");

                /*
                 * FIX:
                 * Khi consultant/designer upload print-ready-file theo estimate nào
                 * thì nên lưu accepted_estimate_id nếu chưa có.
                 * Các bước production phía sau đang dựa vào accepted_estimate_id khá nhiều.
                 */
                if (!request.accepted_estimate_id.HasValue ||
                    request.accepted_estimate_id.Value <= 0)
                {
                    request.accepted_estimate_id = estimateId.Value;
                }
            }

            var ext = Path.GetExtension(fileName)?.Trim().ToLowerInvariant();

            var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".doc", ".docx",
        ".xls", ".xlsx",
        ".ppt", ".pptx",
        ".txt", ".rtf",
        ".ai", ".psd", ".psb", ".eps", ".svg", ".cdr", ".indd",
        ".jpg", ".jpeg", ".png", ".tif", ".tiff",
        ".zip", ".rar", ".7z"
    };

            if (string.IsNullOrWhiteSpace(ext) || !allowedExtensions.Contains(ext))
            {
                throw new ArgumentException(
                    "Unsupported file format. Allowed: pdf, doc, docx, xls, xlsx, ppt, pptx, txt, rtf, ai, psd, psb, eps, svg, cdr, indd, jpg, jpeg, png, tif, tiff, zip, rar, 7z");
            }

            var safeFileName = Path.GetFileName(fileName);

            var publicId = estimateId.HasValue && estimateId.Value > 0
                ? $"print_ready/request_{requestId}/estimate_{estimateId.Value}/file"
                : $"print_ready/request_{requestId}/file";

            var finalContentType = RequestServiceHelper.ResolveContentType(
                safeFileName,
                contentType);

            var url = await _cloudinaryStorage.UploadRawWithPublicIdAsync(
                fileStream,
                safeFileName,
                finalContentType,
                publicId);

            request.print_ready_file = url;

            StampActualConsultant(request);

            int? orderIdForAutoMethodCheck = null;

            /*
             * FIX CHÍNH:
             * Upload print-ready-file được xem là xác nhận layout/file in sẵn sàng.
             * Nhưng request.process_status giữ nguyên như yêu cầu của bạn.
             */
            if (request.order_id.HasValue && request.order_id.Value > 0)
            {
                var ord = await _db.orders
                    .FirstOrDefaultAsync(x => x.order_id == request.order_id.Value, ct);

                if (ord == null)
                    throw new InvalidOperationException("Order not found for this request.");

                var blockedStatus =
                    string.Equals(ord.status, "InProcessing", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ord.status, "Importing", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ord.status, "Delivery", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ord.status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ord.status, "Finished", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ord.status, "Cancelled", StringComparison.OrdinalIgnoreCase);

                if (!blockedStatus)
                {
                    ord.layout_confirmed = true;

                    /*
                     * Sau upload print-ready-file:
                     * - order.status = Pending
                     * - production nếu auto tạo cũng Pending
                     * - chưa tạo task, chưa lập lịch
                     */
                    ord.status = "Pending";
                    ord.is_production_ready = false;

                    orderIdForAutoMethodCheck = ord.order_id;
                }
            }

            await _requestRepo.SaveChangesAsync();

            /*
             * FIX:
             * Sau khi lưu file + confirm layout, tự trigger check method.
             * Nếu chỉ có 1 method khả dụng:
             * - tạo production shell Pending nếu chưa có
             * - điền gm_proposed_method
             * - điền prod_method
             * - điền is_full_process/sub_product_id/sub_product_used_qty/nvl_qty
             * - điền gm_note/mgr_note
             * - production_approval_flow = AUTO_SINGLE_OPTION
             * - order.is_production_ready = true
             *
             * Nếu có 2+ method:
             * - không auto duyệt
             * - chờ GM gọi start-ready như flow cũ.
             */
            if (orderIdForAutoMethodCheck.HasValue)
            {
                QueueRelease(
                    orderIdForAutoMethodCheck.Value,
                    autoApproveSingleMethod: true);
            }

            return url;
        }

        private async Task<material?> ResolveMaterialByCodesAsync(params string?[] codes)
        {
            var normalizedCodes = codes
                .Select(NormalizeMaterialAlias)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var code in normalizedCodes)
            {
                var material = await _materialRepo.GetByCodeAsync(code!);
                if (material != null)
                    return material;
            }

            return null;
        }

        private static string ResolveCoatingMaterialName(string? coatingType)
        {
            var code = (coatingType ?? "").Trim().ToUpperInvariant();

            return code switch
            {
                "KEO_NUOC" => "Keo phủ nước",
                "KEO_DAU" => "Keo phủ dầu",
                "UV" => "Phủ UV",
                "" => "Vật tư phủ",
                _ => coatingType!.Trim()
            };
        }

        private static bom CreateBomLine(
            int orderItemId,
            int estimateId,
            decimal orderQty,
            int? materialId,
            string materialCode,
            string materialName,
            string unit,
            decimal qtyTotal)
        {
            if (orderQty <= 0) orderQty = 1;

            return new bom
            {
                order_item_id = orderItemId,
                material_id = materialId,
                material_code = materialCode.Length <= 20 ? materialCode : materialCode.Substring(0, 20),
                material_name = materialName,
                unit = unit,
                qty_total = qtyTotal,
                qty_per_product = qtyTotal / orderQty,
                source_estimate_id = estimateId
            };
        }

        public void QueueRelease(
    int orderId,
    bool autoApproveSingleMethod = false)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    /*
                     * Flow mới sau upload print-ready-file/layout:
                     * - Không gọi ExecuteAsync.
                     * - Không tạo task.
                     * - Không lập lịch.
                     *
                     * Chỉ tự check method:
                     * - 1 method khả dụng: tạo/fill production shell Pending và auto approve method.
                     * - 2+ method khả dụng: không auto duyệt, chờ GM start-ready.
                     */
                    if (!autoApproveSingleMethod)
                        return;

                    await TryCreatePendingProductionShellAfterLayoutIfSingleMethodAsync(
                        orderId,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "QueueRelease failed. OrderId={OrderId}",
                        orderId);
                }
            });
        }

        private async Task TryCreatePendingProductionShellAfterLayoutIfSingleMethodAsync(
    int orderId,
    CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var productionService = scope.ServiceProvider.GetRequiredService<IProductionService>();

            var ord = await db.orders
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_id == orderId, ct);

            if (ord == null)
            {
                _logger.LogWarning(
                    "[QueueRelease] Skip because order not found. OrderId={OrderId}",
                    orderId);
                return;
            }

            if (!ord.layout_confirmed)
            {
                _logger.LogInformation(
                    "[QueueRelease] Skip because layout_confirmed=false. OrderId={OrderId}",
                    orderId);
                return;
            }

            /*
             * FIX:
             * Không return khi đã có production shell.
             *
             * Lý do:
             * - Có thể production shell đã tồn tại nhưng còn Pending/WAITING_MANAGER/prod_method null.
             * - Nếu return ở đây thì order chỉ có 1 method vẫn không được auto duyệt.
             *
             * Đẩy toàn bộ quyết định vào AutoCreatePendingProductionAfterLayoutIfSingleMethodAsync:
             * - Nếu đã approved thật rồi thì hàm đó tự skip.
             * - Nếu shell chưa approved thì hàm đó sẽ fill data auto.
             */
            var result = await productionService.AutoCreatePendingProductionAfterLayoutIfSingleMethodAsync(
                orderId,
                ct);

            _logger.LogInformation(
                "[QueueRelease] AutoCreatePendingProductionAfterLayout result. OrderId={OrderId}, Created={Created}, Method={Method}, Reason={Reason}",
                orderId,
                result.created,
                result.method,
                result.reason);
        }

        private async Task TryAutoApproveProductionMethodAfterLayoutAsync(
    int orderId,
    CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var productionService = scope.ServiceProvider.GetRequiredService<IProductionService>();
            var hub = scope.ServiceProvider.GetRequiredService<IHubContext<RealtimeHub>>();

            var ord = await db.orders
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_id == orderId, ct);

            if (ord == null)
            {
                _logger.LogWarning(
                    "[AutoApproveProductionMethodAfterLayout] Order not found. OrderId={OrderId}",
                    orderId);
                return;
            }

            if (!ord.layout_confirmed)
            {
                _logger.LogInformation(
                    "[AutoApproveProductionMethodAfterLayout] Skip because layout_confirmed=false. OrderId={OrderId}",
                    orderId);
                return;
            }

            var productions = await db.productions
                .AsNoTracking()
                .Where(x =>
                    x.order_id == orderId &&
                    x.end_date == null &&
                    (
                        x.prod_kind == null ||
                        x.prod_kind != "GROUP"
                    ))
                .OrderByDescending(x => x.prod_id)
                .ToListAsync(ct);

            if (productions.Count == 0)
            {
                _logger.LogWarning(
                    "[AutoApproveProductionMethodAfterLayout] No production found after release. OrderId={OrderId}",
                    orderId);
                return;
            }

            /*
             * Tránh gọi lặp nếu order đã ready và production đã có method rõ ràng.
             */
            var allProductionsAlreadyHaveMethod = productions.All(x =>
                !string.IsNullOrWhiteSpace(x.prod_method) &&
                !string.Equals(x.prod_method, "MANUAL", StringComparison.OrdinalIgnoreCase));

            if (ord.is_production_ready && allProductionsAlreadyHaveMethod)
            {
                _logger.LogInformation(
                    "[AutoApproveProductionMethodAfterLayout] Skip because order already ready and method already approved. OrderId={OrderId}",
                    orderId);
                return;
            }

            try
            {
                /*
                 * Đây chính là API start-ready được trigger tự động.
                 *
                 * proposedMethod = null để service tự resolve option khả dụng:
                 * - chỉ NVL
                 * - chỉ SUB
                 * - chỉ BOTH
                 * - hoặc nhiều option thì chờ manager chọn.
                 */
                var result = await productionService.SetProductionReadyAsync(
                    orderId,
                    true,
                    "Tự động kiểm tra và duyệt method sản xuất sau khi designer xác nhận layout.",
                    null,
                    ct);

                _logger.LogInformation(
                    "[AutoApproveProductionMethodAfterLayout] Done. OrderId={OrderId}, Result={@Result}",
                    orderId,
                    result);

                await hub.Clients.All.SendAsync(
                    "update-ui",
                    new
                    {
                        message = "update UI",
                        order_id = orderId
                    },
                    ct);
            }
            catch (Exception ex)
            {
                /*
                 * Không throw ngược để tránh làm chết background release.
                 * Lỗi sẽ được log, general vẫn có thể xử lý bằng API start-ready thủ công.
                 */
                _logger.LogError(
                    ex,
                    "[AutoApproveProductionMethodAfterLayout] Failed. OrderId={OrderId}",
                    orderId);
            }
        }

        public async Task ExecuteAsync(int orderId, CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var scheduling = scope.ServiceProvider.GetRequiredService<IProductionSchedulingService>();

            var ord = await db.orders
                .FirstOrDefaultAsync(x => x.order_id == orderId, ct)
                ?? throw new InvalidOperationException("Order not found");

            if (!ord.layout_confirmed)
                throw new InvalidOperationException("Layout has not been confirmed");

            var req = await db.order_requests
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_id == orderId, ct)
                ?? throw new InvalidOperationException("Order request not found for this order");

            var existingProdId = await db.productions
                .AsNoTracking()
                .Where(x => x.order_id == orderId && x.end_date == null)
                .OrderByDescending(x => x.prod_id)
                .Select(x => (int?)x.prod_id)
                .FirstOrDefaultAsync(ct);

            if (existingProdId.HasValue)
            {
                var hasTasks = await db.tasks
                    .AsNoTracking()
                    .AnyAsync(x => x.prod_id == existingProdId.Value, ct);

                if (hasTasks)
                {
                    if (string.Equals(ord.status, "Scheduling", StringComparison.OrdinalIgnoreCase))
                    {
                        ord.status = "Scheduled";
                        await db.SaveChangesAsync(ct);
                    }
                    return;
                }
            }

            var productTypeId = await db.product_types
                .AsNoTracking()
                .Where(x => x.code == req.product_type)
                .Select(x => x.product_type_id)
                .FirstOrDefaultAsync(ct);

            if (productTypeId <= 0)
                throw new InvalidOperationException("product_type invalid (cannot map product_type_id)");

            var item = await db.order_items
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderBy(x => x.item_id)
                .FirstOrDefaultAsync(ct);

            await scheduling.ScheduleOrderAsync(
                orderId: orderId,
                productTypeId: productTypeId,
                productionProcessCsv: item?.production_process,
                managerId: 3);

            ord = await db.orders.FirstOrDefaultAsync(x => x.order_id == orderId, ct)
                ?? throw new InvalidOperationException("Order disappeared after schedule");

            if (string.Equals(ord.status, "Scheduling", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ord.status, "LayoutPending", StringComparison.OrdinalIgnoreCase))
            {
                ord.status = "Scheduled";
                await db.SaveChangesAsync(ct);
            }
        }


        private static string? NormalizeMaterialAlias(string? code)
        {
            var c = (code ?? "").Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(c))
                return null;

            return c switch
            {
                "KEO_NUOC" => "KEO_PHU_NUOC",
                "KEO PHU NUOC" => "KEO_PHU_NUOC",
                "KEO PHỦ NƯỚC" => "KEO_PHU_NUOC",
                "KEO_PHU_NUOC" => "KEO_PHU_NUOC",
                "KEO DẦU" => "KEO_PHU_DAU",
                "KEO_DAU" => "KEO_PHU_DAU",
                "KEO_PHU_DAU" => "KEO_PHU_DAU",
                _ => c
            };
        }

        private async Task<cost_estimate?> ResolveEstimateForRequestAsync(order_request req, CancellationToken ct = default)
        {
            if (req.accepted_estimate_id.HasValue && req.accepted_estimate_id.Value > 0)
            {
                var accepted = await _estimateRepo.GetTrackingByIdAsync(req.accepted_estimate_id.Value, ct);
                if (accepted != null && accepted.order_request_id == req.order_request_id)
                    return accepted;
            }

            return await _estimateRepo.GetByOrderRequestIdAsync(req.order_request_id);
        }

        private int? GetActualConsultantUserId()
        {
            return _currentUser.IsConsultant && _currentUser.UserId.HasValue
                ? _currentUser.UserId.Value
                : null;
        }

        private void StampActualConsultant(order_request req)
        {
            var userId = GetActualConsultantUserId();
            if (userId.HasValue)
                req.actual_consultant_user_id = userId.Value;
        }

        public async Task<PagedResultLite<RequestSortedDto>> GetMyRequestsByJwtAsync(
    int page,
    int pageSize,
    CancellationToken ct = default)
        {
            var (_, phoneNumber) = await GetCurrentCustomerPhoneAsync(ct);

            return await _requestRepo.GetRequestsByPhonePagedAsync(
                phone: phoneNumber,
                page: page,
                pageSize: pageSize,
                consultantUserId: null,
                ct: ct);
        }

        public async Task<PagedResultLite<OrderListDto>> GetMyOrdersByJwtAsync(
            int page,
            int pageSize,
            CancellationToken ct = default)
        {
            var (_, phoneNumber) = await GetCurrentCustomerPhoneAsync(ct);

            return await _requestRepo.GetOrdersByPhonePagedAsync(
                phone: phoneNumber,
                page: page,
                pageSize: pageSize,
                ct: ct);
        }

        public async Task<CustomerRequestOrderListDto> GetMyRequestsAndOrdersByJwtAsync(
            int requestPage,
            int requestPageSize,
            int orderPage,
            int orderPageSize,
            CancellationToken ct = default)
        {
            var (userId, phoneNumber) = await GetCurrentCustomerPhoneAsync(ct);

            var requestsTask = _requestRepo.GetRequestsByPhonePagedAsync(
                phone: phoneNumber,
                page: requestPage,
                pageSize: requestPageSize,
                consultantUserId: null,
                ct: ct);

            var ordersTask = _requestRepo.GetOrdersByPhonePagedAsync(
                phone: phoneNumber,
                page: orderPage,
                pageSize: orderPageSize,
                ct: ct);

            await Task.WhenAll(requestsTask, ordersTask);

            return new CustomerRequestOrderListDto
            {
                user_id = userId,
                phone_number = phoneNumber,
                requests = await requestsTask,
                orders = await ordersTask
            };
        }

        private static string? ResolveAssignName(AssignedConsultantSummaryDto? consultant)
        {
            if (consultant == null)
                return null;

            return string.IsNullOrWhiteSpace(consultant.full_name)
                ? null
                : consultant.full_name.Trim();
        }

        private static void ApplyAssignedConsultant(order_request entity, AssignedConsultantSummaryDto consultant, DateTime now)
        {
            entity.assigned_consultant = consultant.user_id;
            entity.assign_name = ResolveAssignName(consultant);
            entity.assigned_at = now;
        }

        private async Task<(int userId, string phoneNumber)> GetCurrentCustomerPhoneAsync(CancellationToken ct = default)
        {
            if (!_currentUser.UserId.HasValue)
                throw new UnauthorizedAccessException("User is not authenticated.");

            var userId = _currentUser.UserId.Value;

            var phone = await _userRepo.GetPhoneNumberByUserIdAsync(userId, ct);

            if (string.IsNullOrWhiteSpace(phone))
                throw new InvalidOperationException("Tài khoản hiện tại chưa có số điện thoại.");

            return (userId, phone.Trim());
        }
    }
}
