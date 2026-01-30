using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities;

[Table("users", Schema = "AMMS_DB")]
public partial class user
{
    public int user_id { get; set; }

    public string username { get; set; } = null!;

    public string password_hash { get; set; } = null!;

    public string? email { get; set; }

    public string? full_name { get; set; }

    public int? role_id { get; set; }

    public bool? is_active { get; set; }

    public string? phone_number { get; set; }

    public DateTime? created_at { get; set; }

    public virtual ICollection<order> orders { get; set; } = new List<order>();

    public virtual ICollection<production> productions { get; set; } = new List<production>();

    public virtual ICollection<purchase> purchases { get; set; } = new List<purchase>();

    public virtual ICollection<quote> quotes { get; set; } = new List<quote>();

    public virtual role? role { get; set; }

    public virtual ICollection<stock_move> stock_moves { get; set; } = new List<stock_move>();
}
