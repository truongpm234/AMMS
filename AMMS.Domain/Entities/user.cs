using System;
using System.Collections.Generic;

namespace AMMS.Domain.Entities;

public partial class user
{
    public int user_id { get; set; }

    public string username { get; set; } = null!;

    public string password_hash { get; set; } = null!;

    public string? full_name { get; set; }

    public int? role_id { get; set; }

    public bool? is_active { get; set; }

    public DateTime? created_at { get; set; }

    public virtual ICollection<customer> customers { get; set; } = new List<customer>();

    public virtual ICollection<order> orders { get; set; } = new List<order>();

    public virtual ICollection<production> productions { get; set; } = new List<production>();

    public virtual ICollection<purchase> purchases { get; set; } = new List<purchase>();

    public virtual ICollection<quote> quotes { get; set; } = new List<quote>();

    public virtual role? role { get; set; }

    public virtual ICollection<stock_move> stock_moves { get; set; } = new List<stock_move>();

    public virtual ICollection<task_log> task_logs { get; set; } = new List<task_log>();

    public virtual ICollection<task> tasks { get; set; } = new List<task>();
}
