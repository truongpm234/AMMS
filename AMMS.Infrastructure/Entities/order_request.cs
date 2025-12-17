using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities;

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

}
