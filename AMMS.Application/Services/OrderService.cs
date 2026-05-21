using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Materials;
using AMMS.Shared.DTOs.Orders;

namespace AMMS.Application.Services
{
    public class OrderService : IOrderService
    {
        private readonly IOrderRepository _orderRepo;

        public OrderService(IOrderRepository orderRepo)
        {
            _orderRepo = orderRepo;
        }

        public async Task<order> GetOrderByCodeAsync(string code)
        {
            var order = await _orderRepo.GetByCodeAsync(code);
            if (order == null)
            {
                throw new Exception("Order not found");
            }
            return order;
        }
        public async Task<PagedResultLite<OrderResponseDto>> GetPagedAsync(int page, int pageSize)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;

            var list = await _orderRepo.GetPagedWithFulfillAsync(skip, pageSize + 1);

            var hasNext = list.Count > pageSize;
            var data = hasNext ? list.Take(pageSize).ToList() : list;

            return new PagedResultLite<OrderResponseDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = data
            };
        }
        public async Task<order> GetByIdAsync(int id)
        {
            var order = await _orderRepo.GetByIdAsync(id);
            if (order == null)
            {
                throw new Exception("Order not found");
            }
            return order;
        }
        public Task<OrderDetailDto?> GetDetailAsync(int id, CancellationToken ct = default)
            => _orderRepo.GetDetailByIdAsync(id, ct);

        public async Task<PagedResultLite<MissingMaterialDto>> GetAllMissingMaterialsAsync(int page, int pageSize, CancellationToken ct = default)
        {
            var result = await _orderRepo.GetAllMissingMaterialsAsync(page, pageSize, ct);

            static decimal RoundUpToTens(decimal value)
            {
                if (value <= 0m) return 0m;
                return Math.Ceiling(value / 10m) * 10m; 
            }

            if (result.Data == null || result.Data.Count == 0)
                return result;

            foreach (var x in result.Data)
            {
                var missingBase = x.quantity;
                if (missingBase < 0m) missingBase = 0m;

                var withBuffer = missingBase * 1.10m;

                var rounded = RoundUpToTens(withBuffer);

                decimal unitPrice = 0m;
                if (missingBase > 0m && x.total_price > 0m)
                {
                    unitPrice = x.total_price / missingBase;
                }

                x.quantity = rounded;
                x.total_price = Math.Round(rounded * unitPrice, 2);
            }

            return result;
        }

        public Task<string> DeleteDesignFilePath(int orderRequestId)
        {
            return _orderRepo.DeleteDesignFilePath(orderRequestId);
        }

        public Task<List<order>> GetAllOrderWithStatusInProcess()
        {
            return _orderRepo.GetAllOrderInprocessStatus();
        }

        public async Task<PagedResultLite<OrderProductionTrackingDto>> GetProductionTrackingByOrderStatusAsync(
    string status,
    int page,
    int pageSize,
    CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(status))
                throw new ArgumentException("status không được để trống");

            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;
            if (pageSize > 200) pageSize = 200;

            status = status.Trim();

            var raw = await _orderRepo.GetProductionTrackingByOrderStatusAsync(
                status,
                page,
                pageSize,
                ct);

            var data = raw.Orders.Select(o =>
            {
                var requestIds = raw.Requests
                    .Where(r => r.order_id == o.order_id)
                    .Select(r => r.order_request_id)
                    .Distinct()
                    .ToList();

                var linkedProdIds = raw.ProdOrders
                    .Where(x => x.order_id == o.order_id)
                    .SelectMany(x =>
                        x.single_prod_id == null
                            ? new[] { x.prod_id }
                            : new[] { x.prod_id, x.single_prod_id.Value })
                    .ToHashSet();

                if (o.production_id != null)
                    linkedProdIds.Add(o.production_id.Value);

                var relatedProductions = raw.Productions
                    .Where(p =>
                        (p.order_id != null && p.order_id.Value == o.order_id) ||
                        linkedProdIds.Contains(p.prod_id))
                    .GroupBy(p => p.prod_id)
                    .Select(g => g.First())
                    .OrderBy(p => p.planned_start_date)
                    .ThenBy(p => p.prod_id)
                    .ToList();

                return new OrderProductionTrackingDto
                {
                    request_id = requestIds.Count > 0 ? requestIds[0] : null,

                    order_id = o.order_id,
                    order_status = o.status,

                    productions = relatedProductions.Select(p => new ProductionTrackingDto
                    {
                        prod_id = p.prod_id,
                        code = p.code,
                        order_id = p.order_id,
                        manager_id = p.manager_id,
                        end_date = p.end_date,
                        status = p.status,
                        product_type_id = p.product_type_id,
                        note = p.note,
                        created_at = p.created_at,
                        planned_start_date = p.planned_start_date,
                        actual_start_date = p.actual_start_date,
                        is_full_process = p.is_full_process,
                        sub_product_used_qty = p.sub_product_used_qty,
                        import_recieve_path = p.import_recieve_path,
                        sub_product_id = p.sub_product_id,
                        nvl_qty = p.nvl_qty,
                        prod_method = p.prod_method,
                        gm_note = p.gm_note,
                        mgr_note = p.mgr_note,
                        prod_kind = p.prod_kind,
                        group_process_codes = p.group_process_codes,
                        group_total_qty = p.group_total_qty,
                        gm_proposed_method = p.gm_proposed_method,

                        tasks = raw.Tasks
                            .Where(t => t.prod_id == p.prod_id)
                            .OrderBy(t => t.seq_num)
                            .ThenBy(t => t.task_id)
                            .Select(t => new TaskTrackingDto
                            {
                                task_id = t.task_id,
                                prod_id = t.prod_id,
                                name = t.name,
                                seq_num = t.seq_num,
                                status = t.status,
                                machine = t.machine,
                                start_time = t.start_time,
                                end_time = t.end_time,
                                process_id = t.process_id,
                                planned_start_time = t.planned_start_time,
                                planned_end_time = t.planned_end_time,
                                reason = t.reason,
                                is_taken_sub_product = t.is_taken_sub_product,
                                input_mode = t.input_mode,

                                task_logs = raw.TaskLogs
                                    .Where(l => l.task_id == t.task_id)
                                    .OrderByDescending(l => l.log_time)
                                    .ThenByDescending(l => l.log_id)
                                    .Select(l => new TaskLogTrackingDto
                                    {
                                        log_id = l.log_id,
                                        task_id = l.task_id,
                                        scanned_code = l.scanned_code,
                                        action_type = l.action_type,
                                        qty_good = l.qty_good,
                                        log_time = l.log_time,
                                        scanned_by_user_id = l.scanned_by_user_id,
                                        material_usage_json = l.material_usage_json,
                                        reason = l.reason,
                                        report_image_url = l.report_image_url,
                                        reference_input_json = l.reference_input_json,
                                        output_json = l.output_json
                                    })
                                    .ToList()
                            })
                            .ToList()
                    })
                    .ToList()
                };
            }).ToList();

            return new PagedResultLite<OrderProductionTrackingDto>
            {
                Page = raw.Page,
                PageSize = raw.PageSize,
                HasNext = raw.HasNext,
                Data = data
            };
        }
    }
}
