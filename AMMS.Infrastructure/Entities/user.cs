using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace AMMS.Infrastructure.Entities;

[Table("users", Schema = "AMMS_DB")]
public partial class user
{
    public int user_id { get; set; }

    public string username { get; set; } = null!;

    public string password_hash { get; set; } = null!;

    public string? email { get; set; }

    public string? full_name { get; set; }

    [Column(TypeName = "jsonb")]
    public List<string>? address { get; set; } = new();

    public int? role_id { get; set; }

    public bool? is_active { get; set; }

    public string? phone_number { get; set; }

    public DateTime? created_at { get; set; }

    public virtual ICollection<production> productions { get; set; } = new List<production>();

    public virtual role? role { get; set; }

    public virtual ICollection<stock_move> stock_moves { get; set; } = new List<stock_move>();

    public virtual ICollection<order_request> assigned_order_requests { get; set; } = new List<order_request>();

}
