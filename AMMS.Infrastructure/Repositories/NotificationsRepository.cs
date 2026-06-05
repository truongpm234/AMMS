using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Infrastructure.Repositories
{
    public class NotificationsRepository
    {
        private readonly AppDbContext _db;
        public NotificationsRepository(AppDbContext db)
        {
            _db = db;
        }

        public async Task<List<notification>> GetNotificationByRoleId(int role_id)
        {
            var res = await _db.notifications.Where(n => n.RoleId == role_id).ToListAsync();
            return res;
        }

        public async Task<List<notification>> StaffGetNoti(string role)
        {
            return await _db.notifications
                .FromSqlInterpolated($@"
            SELECT *
            FROM ""AMMS_DB"".notifications
            WHERE ""AMMS_DB"".unaccent(lower(status::text))
            LIKE ""AMMS_DB"".unaccent(lower({'%' + role + '%'}::text))
        ")
                .ToListAsync();
        }

        public async Task<List<notification>> GetConsultantNotificationUserId(int role_id, int? user_id)
        {
            var res = await _db.notifications.Where(n => n.RoleId == role_id && n.UserId == user_id).ToListAsync();
            return res;
        }

        public async Task<string> CreateNoti(int role_id, string content, int? user_id, int order_request_id, string status)
        {
            var res = new notification();
            res.RoleId = role_id;
            res.OrderRequestId = order_request_id;
            res.Content = content;
            res.UserId = user_id;
            res.Time = DateTime.UtcNow;
            res.IsCheck = false;
            res.Status = status;
            await _db.notifications.AddAsync(res);
            _db.SaveChanges();
            return "Success";
        }

        public async Task<string> UpdateNotiManagerApprove(int requestId)
        {
            var res = await _db.notifications.FirstOrDefaultAsync(n => n.OrderRequestId == requestId && n.RoleId == 2);
            if (res != null)
            {
                res.IsCheck = true;
                res.Status = "";
                return "is read";
            }
            return "Failed";
        }

        public async Task<bool> FindAsync(int id)
        {
            var res = await _db.notifications.FirstOrDefaultAsync(n => n.Id == id);
            if (res != null)
            {
                res.IsCheck = true;
                await _db.SaveChangesAsync();
                return true;
            }
            return false;
        }
    }
}
