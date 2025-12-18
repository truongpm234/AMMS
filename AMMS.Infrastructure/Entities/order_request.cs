using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities;

[Table("order_request", Schema = "AMMS_DB")]

public partial class order_request
{
    public int order_request_id { get; set; }

    public string? customer_name { get; set; }

    public string? customer_phone { get; set; }

    public string? customer_email { get; set; }

    public DateTime? delivery_date { get; set; }

    public string? product_name { get; set; }

    public int? quantity { get; set; }

    public string? description { get; set; }

    public string? design_file_path { get; set; }

    public DateTime? order_request_date { get; set; }

    public string? province { get; set; }       

    public string? district { get; set; }     
    
    public string? detail_address { get; set; }

    public string process_status { get; set; }

    // Loại sản phẩm in (để xác định hao hụt in)
    // "GACH_1MAU", "GACH_XUAT_KHAU_DON_GIAN", "GACH_XUAT_KHAU_TERACON", 
    // "GACH_NOI_DIA_4SP", "GACH_NOI_DIA_6SP", "HOP_MAU_1LUOT_DON_GIAN", 
    // "HOP_MAU_1LUOT_THUONG", "HOP_MAU_1LUOT_KHO", "HOP_MAU_AQUA_DOI", "HOP_MAU_2LUOT"
    public string? product_type { get; set; }

    // Số cao bản (chỉ áp dụng cho hộp màu)
    public int? number_of_plates { get; set; }

    // Danh sách công đoạn (JSON string hoặc CSV)
    // Ví dụ: "IN,BE,BOI,DAN" hoặc JSON: ["IN","BE","BOI","DAN"]
    public string? production_processes { get; set; }

    // Loại phủ: "KEO_NUOC", "KEO_DAU", "NONE"
    public string? coating_type { get; set; }

    // Có cán màng không
    public bool has_lamination { get; set; }

    public int? order_id { get; set; }

    public virtual order? order { get; set; }
}
