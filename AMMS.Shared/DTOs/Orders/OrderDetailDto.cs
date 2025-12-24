using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Orders
{
    public class OrderDetailDto
    {
        // Header
        public int Order_id { get; set; }
        public string code { get; set; } = null!;
        public string status { get; set; } = null!;
        public string payment_status { get; set; } = null!;
        public DateTime order_date { get; set; }              
        public DateTime? delivery_date { get; set; }         

        // Khách hàng
        public string customer_name { get; set; } = string.Empty;  
        public string? customer_email { get; set; }                
        public string? customer_phone { get; set; }                 

        // Sản phẩm
        public string product_name { get; set; } = string.Empty;
        public string product_type { get; set; } = string.Empty;
        public int quantity { get; set; }

        // Lịch sản xuất
        public DateTime? production_start_date { get; set; }  
        public DateTime? production_end_date { get; set; }     
        public string approver_name { get; set; } = string.Empty; 

        public string? specification { get; set; }           
        public string? note { get; set; }                    

        // Tài chính
        public decimal rush_amount { get; set; }              
        public decimal estimate_total { get; set; }          

        // File
        public string? file_url{ get; set; }          
        public string? contract_file { get; set; }         
    }
}
