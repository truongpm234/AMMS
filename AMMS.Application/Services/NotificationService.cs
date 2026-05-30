using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Application.Services
{
    public class NotificationService
    {
        private readonly NotificationsRepository _notiRepo;
        private readonly AppDbContext _db;

        public NotificationService(NotificationsRepository repo, AppDbContext db)
        {
            _notiRepo = repo;
            _db = db;
        }

        public async Task<List<notification>> GetNotiByRoleId(int id)
        {
            return await _notiRepo.GetNotificationByRoleId(id);
        }

        public async Task<List<notification>> StaffGetNoti(string role)
        {
            var res = await _notiRepo.StaffGetNoti(role);
            return res;
        }

        public async Task<List<notification>> GetConsultantNotiByUserId(int id, int? user_id)
        {
            return await _notiRepo.GetConsultantNotificationUserId(id, user_id);
        }

        public async Task<string> CreateNotfi(int role_id, string content, int? user_id, int order_request_id, string status)
        {
            return await _notiRepo.CreateNoti(role_id, content, user_id, order_request_id, status);
        }

        public async Task<bool> MarkAsRead(int id)
        {
            var res = await _notiRepo.FindAsync(id);
            return res;
        }

        public async Task<bool> MarkAllAsRead(int role_id, int? user_id)
        {
            // Lấy query danh sách thông báo chưa đọc dựa theo role_id (giống API GetNotiByRoleId)
            var query = _db.notifications.Where(n => n.RoleId == role_id && n.IsCheck == false);

            // Nếu có user_id (trường hợp của role consultant) thì lọc thêm
            if (user_id.HasValue)
            {
                query = query.Where(n => n.UserId == user_id.Value);
            }

            var unreadNotis = await query.ToListAsync();

            if (!unreadNotis.Any()) return false;

            // Đổi toàn bộ trạng thái thành đã đọc
            foreach (var noti in unreadNotis)
            {
                noti.IsCheck = true;
            }

            _db.notifications.UpdateRange(unreadNotis);
            await _db.SaveChangesAsync();

            return true;
        }

    }
}
