using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Entities.AMMS.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Text.Json;

namespace AMMS.Infrastructure.DBContext;

public partial class AppDbContext : DbContext
{
    public AppDbContext()
    {
    }

    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<bom> boms { get; set; }

    public virtual DbSet<material> materials { get; set; }

    public virtual DbSet<order> orders { get; set; }

    public virtual DbSet<order_item> order_items { get; set; }

    public virtual DbSet<order_request> order_requests { get; set; }

    public virtual DbSet<product_type> product_types { get; set; }

    public virtual DbSet<product_type_process> product_type_processes { get; set; }

    public virtual DbSet<production> productions { get; set; }

    public virtual DbSet<quote> quotes { get; set; }

    public virtual DbSet<role> roles { get; set; }

    public virtual DbSet<stock_move> stock_moves { get; set; }

    public virtual DbSet<supplier> suppliers { get; set; }

    public virtual DbSet<task> tasks { get; set; }

    public virtual DbSet<task_log> task_logs { get; set; }

    public virtual DbSet<user> users { get; set; }

    public virtual DbSet<cost_estimate> cost_estimates { get; set; }

    public virtual DbSet<machine> machines { get; set; }

    public virtual DbSet<supplier_material> supplier_materials { get; set; }

    public virtual DbSet<payment> payments { get; set; } = null!;

    public virtual DbSet<cost_estimate_process> cost_estimate_processes { get; set; }

    public virtual DbSet<product_template> product_templates { get; set; } = null!;

    public virtual DbSet<missing_material> missing_materials { get; set; } = null!;

    public virtual DbSet<notification> notifications { get; set; } = null!;

    public virtual DbSet<production_calendar> production_calendars { get; set; }

    public virtual DbSet<estimate_config> estimate_config { get; set; } = null!;

    public virtual DbSet<product> products { get; set; } = null!;

    public virtual DbSet<sub_product> sub_products { get; set; } = null!;

    public DbSet<prod_order> prod_orders { get; set; } = null!;

    public DbSet<task_link> task_links { get; set; } = null!;

    public DbSet<task_qty> task_qtys { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<bom>(entity =>
        {
            entity.HasKey(e => e.bom_id).HasName("boms_pkey");

            entity.Property(e => e.qty_per_product).HasPrecision(10, 4);
            entity.Property(e => e.wastage_percent)
                .HasPrecision(5, 2)
                .HasDefaultValueSql("5.0");

            entity.HasOne(d => d.material).WithMany(p => p.boms)
                .HasForeignKey(d => d.material_id)
                .HasConstraintName("boms_material_id_fkey");

            entity.HasOne(d => d.order_item).WithMany(p => p.boms)
                .HasForeignKey(d => d.order_item_id)
                .HasConstraintName("boms_order_item_id_fkey");
        });

        modelBuilder.Entity<material>(entity =>
        {
            entity.HasKey(e => e.material_id).HasName("materials_pkey");
            entity.HasIndex(e => e.code, "materials_code_key").IsUnique();
            entity.Property(e => e.code).HasMaxLength(50);
            entity.Property(e => e.cost_price).HasPrecision(15, 2);
            entity.Property(e => e.min_stock).HasPrecision(10, 2).HasDefaultValueSql("100");
            entity.Property(e => e.name).HasMaxLength(150);
            entity.Property(e => e.stock_qty).HasPrecision(10, 2).HasDefaultValueSql("0");
            entity.Property(e => e.unit).HasMaxLength(20);
            entity.Property(e => e.type).HasMaxLength(50);
        });

        modelBuilder.Entity<machine>(entity =>
        {
            entity.HasKey(e => e.machine_id).HasName("machines_pkey");
            entity.HasIndex(e => e.machine_code, "machines_machine_code_key").IsUnique();
            entity.Property(e => e.process_name).HasMaxLength(100);
            entity.Property(e => e.machine_code).HasMaxLength(50);
            entity.Property(e => e.is_active).HasDefaultValue(true);
        });

        modelBuilder.Entity<order>(entity =>
        {
            entity.HasKey(e => e.order_id).HasName("orders_pkey");

            entity.HasIndex(e => e.code, "orders_code_key").IsUnique();

            entity.Property(e => e.code).HasMaxLength(20);
            entity.Property(e => e.delivery_date).HasColumnType("timestamp without time zone");
            entity.Property(e => e.confirmed_delivery_at).HasColumnType("timestamp without time zone");
            entity.Property(e => e.order_date)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.payment_status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'Unpaid'::character varying");
            entity.Property(e => e.status)
                .HasMaxLength(30)
                .HasDefaultValueSql("'New'::character varying");
            entity.Property(e => e.total_amount).HasPrecision(15, 2);

            entity.HasOne(d => d.quote).WithMany(p => p.orders)
                .HasForeignKey(d => d.quote_id)
                .HasConstraintName("orders_quote_id_fkey");

            entity.HasOne(o => o.production)
                .WithMany()
                .HasForeignKey(o => o.production_id)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_orders_production");

            entity.Property(e => e.layout_confirmed)
                .HasDefaultValue(false);

            entity.HasIndex(e => e.layout_confirmed)
                .HasDatabaseName("ix_orders_layout_confirmed");
        });

        modelBuilder.Entity<order_item>(entity =>
        {
            entity.HasKey(e => e.item_id).HasName("order_items_pkey");
            entity.Property(e => e.product_name).HasMaxLength(200);
            entity.HasOne(d => d.order).WithMany(p => p.order_items)
                .HasForeignKey(d => d.order_id)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("order_items_order_id_fkey");
            entity.HasOne(d => d.product_type).WithMany(p => p.order_items)
                .HasForeignKey(d => d.product_type_id)
                .HasConstraintName("order_items_product_type_id_fkey");
        });

        modelBuilder.Entity<order_request>(entity =>
        {
            entity.HasKey(e => e.order_request_id).HasName("order_request_pkey");
            entity.Property(e => e.customer_email).HasMaxLength(100);
            entity.Property(e => e.customer_name).HasMaxLength(100);
            entity.Property(e => e.customer_phone).HasMaxLength(20);
            entity.Property(e => e.delivery_date).HasColumnType("timestamp without time zone");
            entity.Property(e => e.assigned_at).HasColumnType("timestamp without time zone");
            entity.Property(e => e.order_request_date).HasColumnType("timestamp without time zone");
            entity.Property(e => e.estimate_finish_date).HasColumnType("timestamp without time zone");
            entity.Property(e => e.verified_at).HasColumnName("verified_at").HasColumnType("timestamp without time zone");
            entity.Property(e => e.quote_expires_at).HasColumnName("quote_expire_at").HasColumnType("timestamp without time zone");
            entity.Property(e => e.product_name).HasMaxLength(200);
            entity.Property(e => e.product_type).HasMaxLength(50);
            entity.Property(e => e.message_to_customer).HasColumnName("message_to_customer");
            entity.Property(e => e.number_of_plates).HasDefaultValue(0);
            entity.Property(e => e.delivery_date_change_reason).HasColumnType("text");
            entity.HasOne(d => d.order)
                .WithMany()
                .HasForeignKey(d => d.order_id)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_order_request_order");
            entity.HasOne(d => d.quote)
                .WithMany()
                .HasForeignKey(d => d.quote_id)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_order_request_quote");
            entity.HasOne(x => x.assigned_consultants)
    .WithMany(x => x.assigned_order_requests)
    .HasForeignKey(x => x.assigned_consultant)
    .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<product_type>(entity =>
        {
            entity.HasKey(e => e.product_type_id).HasName("product_types_pkey");
            entity.HasIndex(e => e.code, "product_types_code_key").IsUnique();
            entity.Property(e => e.code).HasMaxLength(50);
            entity.Property(e => e.is_active).HasDefaultValue(true);
            entity.Property(e => e.name).HasMaxLength(100);
        });

        modelBuilder.Entity<product_type_process>(entity =>
        {
            entity.HasKey(e => e.process_id).HasName("product_type_process_pkey");
            entity.ToTable("product_type_process", "AMMS_DB");
            entity.Property(e => e.is_active).HasDefaultValue(true);
            entity.Property(e => e.machine).HasMaxLength(50);
            entity.Property(e => e.process_name).HasMaxLength(100);

            entity.HasOne(d => d.product_type)
                .WithMany(p => p.product_type_processes)
                .HasForeignKey(d => d.product_type_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("product_type_process_product_type_id_fkey");
        });

        modelBuilder.Entity<production>(entity =>
        {
            entity.ToTable("productions", "AMMS_DB");

            entity.HasKey(e => e.prod_id).HasName("productions_pkey");
            entity.HasIndex(e => e.code, "productions_code_key").IsUnique();
            entity.Property(e => e.code).HasMaxLength(20);
            entity.Property(e => e.end_date).HasColumnType("timestamp without time zone");
            entity.Property(e => e.actual_start_date).HasColumnType("timestamp without time zone");
            entity.Property(e => e.planned_start_date).HasColumnType("timestamp without time zone");
            entity.Property(e => e.created_at).HasColumnType("timestamp without time zone");
            entity.Property(e => e.status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'Planned'::character varying");

            entity.HasOne(d => d.manager).WithMany(p => p.productions)
                .HasForeignKey(d => d.manager_id)
                .HasConstraintName("productions_manager_id_fkey");

            entity.HasOne(d => d.order).WithMany(p => p.productions)
                .HasForeignKey(d => d.order_id)
                .HasConstraintName("productions_order_id_fkey");

            entity.HasOne(d => d.product_type).WithMany(p => p.productions)
                .HasForeignKey(d => d.product_type_id)
                .HasConstraintName("productions_product_type_id_fkey");
            entity.Property(e => e.import_recieve_path)
    .HasColumnType("text");
            entity.Property(e => e.sub_product_id);

            entity.Property(e => e.gm_proposed_method)
                .HasColumnName("gm_proposed_method")
                .HasMaxLength(10);

            entity.HasOne(d => d.sub_product)
                .WithMany()
                .HasForeignKey(d => d.sub_product_id)
                .HasConstraintName("fk_productions_sub_product");
        });

      
        modelBuilder.Entity<quote>(entity =>
        {
            entity.HasKey(e => e.quote_id).HasName("quotes_pkey");

            entity.Property(e => e.created_at)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'Draft'::character varying");
            entity.Property(e => e.total_amount).HasPrecision(15, 2);

            entity.HasOne(q => q.order_request)
                .WithMany()
                .HasForeignKey(q => q.order_request_id)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<role>(entity =>
        {
            entity.HasKey(e => e.role_id).HasName("roles_pkey");

            entity.HasIndex(e => e.role_name, "roles_role_name_key").IsUnique();

            entity.Property(e => e.role_name).HasMaxLength(50);
        });

        modelBuilder.Entity<stock_move>(entity =>
        {
            entity.HasKey(e => e.move_id).HasName("stock_moves_pkey");

            entity.Property(e => e.move_date)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.qty).HasPrecision(10, 2);
            entity.Property(e => e.ref_doc).HasMaxLength(50);
            entity.Property(e => e.type).HasMaxLength(10);

            entity.HasOne(d => d.material).WithMany(p => p.stock_moves)
                .HasForeignKey(d => d.material_id)
                .HasConstraintName("stock_moves_material_id_fkey");

            entity.HasOne(d => d.user).WithMany(p => p.stock_moves)
                .HasForeignKey(d => d.user_id)
                .HasConstraintName("stock_moves_user_id_fkey");

            entity.HasIndex(e => new { e.type, e.ref_doc })
                .IsUnique()
                .HasDatabaseName("ux_stock_moves_type_ref_doc");
        });

        modelBuilder.Entity<supplier>(entity =>
        {
            entity.HasKey(e => e.supplier_id).HasName("suppliers_pkey");

            entity.Property(e => e.contact_person).HasMaxLength(100);
            entity.Property(e => e.email).HasMaxLength(100);
            entity.Property(e => e.main_material_type).HasMaxLength(50);
            entity.Property(e => e.name).HasMaxLength(150);
            entity.Property(e => e.phone).HasMaxLength(20);
            entity.Property(e => e.rating)
            .HasColumnType("numeric(3,2)")
            .HasDefaultValue(0);
        });

        modelBuilder.Entity<task>(entity =>
        {
            entity.HasKey(e => e.task_id).HasName("tasks_pkey");

            entity.Property(e => e.machine).HasMaxLength(50);
            entity.Property(e => e.name).HasMaxLength(100);
            entity.Property(e => e.status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'Unassigned'::character varying");
            entity.HasOne(d => d.process).WithMany(p => p.tasks)
                .HasForeignKey(d => d.process_id)
                .HasConstraintName("tasks_process_id_fkey");
            entity.HasOne(d => d.prod).WithMany(p => p.tasks)
                .HasForeignKey(d => d.prod_id)
                .HasConstraintName("tasks_prod_id_fkey");
            entity.Property(x => x.planned_start_time)
                .HasColumnType("timestamp without time zone");
            entity.Property(x => x.planned_end_time)
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.start_time).HasColumnType("timestamp without time zone");
            entity.Property(e => e.end_time).HasColumnType("timestamp without time zone");
            entity.HasIndex(x => new { x.machine, x.planned_end_time })
             .HasDatabaseName("ix_tasks_machine_planned_end");
            entity.Property(e => e.reason)
    .HasColumnType("text");
            entity.Property(e => e.is_taken_sub_product)
             .IsRequired()
             .HasDefaultValue(false);
        });

        modelBuilder.Entity<task_log>(entity =>
        {
            entity.HasKey(e => e.log_id).HasName("task_logs_pkey");

            entity.Property(e => e.action_type)
                .HasMaxLength(20)
                .HasDefaultValueSql("'Finish'::character varying");
            entity.Property(e => e.log_time)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.qty_good).HasDefaultValue(0);
            entity.Property(e => e.scanned_code).HasColumnType("text");

            entity.HasOne(d => d.task).WithMany(p => p.task_logs)
                .HasForeignKey(d => d.task_id)
                .HasConstraintName("task_logs_task_id_fkey");
            entity.Property(e => e.material_usage_json)
                .HasColumnType("jsonb");
            entity.Property(e => e.reason)
    .HasColumnType("text");
        });

        modelBuilder.Entity<user>(entity =>
        {
            entity.HasKey(e => e.user_id).HasName("users_pkey");

            entity.HasIndex(e => e.username, "users_username_key").IsUnique();

            entity.Property(e => e.created_at)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");

            entity.Property(e => e.full_name)
                .HasMaxLength(100);

            entity.Property(e => e.is_active)
                .HasDefaultValue(true);

            entity.Property(e => e.password_hash)
                .HasMaxLength(255);

            entity.Property(e => e.username)
                .HasMaxLength(50);

            entity.Property(e => e.address)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null)
                         ?? new List<string>()
                )
                .Metadata.SetValueComparer(
                    new ValueComparer<List<string>>(
                        (c1, c2) => c1!.SequenceEqual(c2!),
                        c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                        c => c.ToList()
                    )
                );

            entity.HasOne(d => d.role)
                .WithMany(p => p.users)
                .HasForeignKey(d => d.role_id)
                .HasConstraintName("users_role_id_fkey");
        });

        modelBuilder.Entity<cost_estimate>(entity =>
        {
            entity.HasKey(e => e.estimate_id).HasName("cost_estimate_pkey");

            entity.Property(e => e.paper_cost).HasPrecision(18, 2);
            entity.Property(e => e.paper_sheets_used).HasDefaultValue(0);
            entity.Property(e => e.paper_unit_price).HasPrecision(18, 2).HasDefaultValue(0);
            entity.Property(e => e.ink_cost).HasPrecision(18, 2).HasDefaultValue(0);
            entity.Property(e => e.ink_weight_kg).HasPrecision(18, 4).HasDefaultValue(0);
            entity.Property(e => e.ink_rate_per_m2).HasPrecision(18, 6).HasDefaultValue(0);
            entity.Property(e => e.coating_glue_cost).HasPrecision(18, 2).HasDefaultValue(0);
            entity.Property(e => e.coating_glue_weight_kg).HasPrecision(18, 4).HasDefaultValue(0);
            entity.Property(e => e.coating_glue_rate_per_m2).HasPrecision(18, 6).HasDefaultValue(0);
            entity.Property(e => e.coating_type).HasMaxLength(20).HasDefaultValue("NONE");
            entity.Property(e => e.mounting_glue_cost).HasPrecision(18, 2).HasDefaultValue(0);
            entity.Property(e => e.mounting_glue_weight_kg).HasPrecision(18, 4).HasDefaultValue(0);
            entity.Property(e => e.mounting_glue_rate_per_m2).HasPrecision(18, 6).HasDefaultValue(0);
            entity.Property(e => e.lamination_cost).HasPrecision(18, 2).HasDefaultValue(0);
            entity.Property(e => e.lamination_weight_kg).HasPrecision(18, 4).HasDefaultValue(0);
            entity.Property(e => e.lamination_rate_per_m2).HasPrecision(18, 6).HasDefaultValue(0);
            entity.Property(e => e.material_cost).HasPrecision(18, 2).HasDefaultValue(0);
            entity.Property(e => e.base_cost).HasPrecision(18, 2);
            entity.Property(e => e.is_rush).HasDefaultValue(false);
            entity.Property(e => e.rush_percent).HasPrecision(5, 2).HasDefaultValue(0);
            entity.Property(e => e.rush_amount).HasPrecision(18, 2).HasDefaultValue(0);
            entity.Property(e => e.days_early).HasDefaultValue(0);
            entity.Property(e => e.subtotal).HasPrecision(18, 2).HasDefaultValue(0);
            entity.Property(e => e.discount_percent).HasPrecision(5, 2).HasDefaultValue(0);
            entity.Property(e => e.discount_amount).HasPrecision(18, 2).HasDefaultValue(0);
            entity.Property(e => e.final_total_cost).HasPrecision(18, 2).HasDefaultValue(0);
            entity.Property(e => e.ink_type_names).HasColumnType("text");
            entity.Property(e => e.alternative_material_reason).HasColumnType("text");
            entity.Property(e => e.created_at)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");

            entity.Property(e => e.estimated_finish_date)
                .HasColumnType("timestamp without time zone");

            entity.Property(e => e.desired_delivery_date)
                .HasColumnType("timestamp without time zone");

            entity.Property(e => e.sheets_required).HasDefaultValue(0);
            entity.Property(e => e.sheets_waste).HasDefaultValue(0);
            entity.Property(e => e.sheets_total).HasDefaultValue(0);
            entity.Property(e => e.total_area_m2).HasPrecision(18, 4).HasDefaultValue(0);
            entity.HasOne(d => d.order_request)
                .WithMany()
                .HasForeignKey(d => d.order_request_id)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_cost_estimate_order_request");

            entity.Property(x => x.deposit_amount)
                .HasColumnName("deposit_amount")
                .HasColumnType("numeric(18,2)");

            entity.Property(e => e.previous_estimate_id)
                .HasColumnName("previous_estimate_id");

            entity.HasAlternateKey(e => new { e.estimate_id, e.order_request_id })
                .HasName("uq_cost_estimate_estimate_id_order_request_id");

            entity.HasOne(e => e.previous_estimate)
                .WithMany(e => e.revised_estimates)
                .HasForeignKey(e => new { e.previous_estimate_id, e.order_request_id })
                .HasPrincipalKey(e => new { e.estimate_id, e.order_request_id })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_cost_estimate_previous_estimate_same_request");
        });

        modelBuilder.Entity<supplier_material>(entity =>
        {
            entity.ToTable("supplier_materials", "AMMS_DB");
            entity.HasKey(x => new { x.supplier_id, x.material_id })
                  .HasName("supplier_materials_pkey");

            entity.Property(x => x.is_active).HasDefaultValue(true);
            entity.Property(x => x.created_at)
                  .HasDefaultValueSql("CURRENT_TIMESTAMP")
                  .HasColumnType("timestamp without time zone");

            entity.HasOne(x => x.supplier)
                  .WithMany(s => s.supplier_materials)
                  .HasForeignKey(x => x.supplier_id)
                  .OnDelete(DeleteBehavior.Cascade)
                  .HasConstraintName("supplier_materials_supplier_id_fkey");

            entity.HasOne(x => x.material)
                  .WithMany(m => m.supplier_materials)
                  .HasForeignKey(x => x.material_id)
                  .OnDelete(DeleteBehavior.Cascade)
                  .HasConstraintName("supplier_materials_material_id_fkey");
        });

        modelBuilder.Entity<payment>(entity =>
        {
            entity.HasKey(e => e.payment_id).HasName("payments_pkey");
            entity.Property(e => e.created_at).HasColumnType("timestamp without time zone");
            entity.Property(e => e.updated_at).HasColumnType("timestamp without time zone");
            entity.Property(e => e.paid_at).HasColumnType("timestamp without time zone");
            entity.ToTable("payments", "AMMS_DB");

            entity.Property(e => e.payment_id)
                  .ValueGeneratedOnAdd();

            entity.Property(e => e.amount).HasColumnType("numeric(18,2)");
            entity.Property(e => e.payos_raw).HasColumnType("jsonb");

            entity.HasOne(d => d.order_request)
                  .WithMany(p => p.payments)
                  .HasForeignKey(d => d.order_request_id)
                  .OnDelete(DeleteBehavior.Cascade)
                  .HasConstraintName("fk_payments_order_request");
        });

        modelBuilder.Entity<cost_estimate_process>(entity =>
        {
            entity.HasKey(e => e.process_cost_id);
            entity.HasOne(e => e.estimate)
                .WithMany(e => e.process_costs)
                .HasForeignKey(e => e.estimate_id)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.quantity)
                .HasPrecision(18, 4);
            entity.Property(e => e.unit_price)
                .HasPrecision(18, 2);
            entity.Property(e => e.total_cost)
                .HasPrecision(18, 2);
            entity.Property(e => e.created_at).HasColumnType("timestamp without time zone");
        });

        modelBuilder.Entity<product_template>(entity =>
        {
            entity.HasKey(e => e.design_profile_id);
            entity.HasOne(e => e.product_type)
                  .WithMany(p => p.product_type_design_profiles)
                  .HasForeignKey(e => e.product_type_id);
        });

        modelBuilder.Entity<notification>(entity =>
        {
            entity.ToTable("notifications", "AMMS_DB");

            entity.HasKey(e => e.Id).HasName("notifications_pkey");

            entity.Property(e => e.Id)
                  .HasColumnName("id")
                  .ValueGeneratedOnAdd();

            entity.Property(e => e.Content)
                  .HasColumnName("content")
                  .HasColumnType("text")
                  .IsRequired();

            entity.Property(e => e.UserId)
                  .HasColumnName("user_id");

            entity.Property(e => e.RoleId)
                  .HasColumnName("role_id");

            entity.Property(e => e.OrderRequestId)
                  .HasColumnName("order_request_id");

            entity.Property(e => e.Time)
                  .HasColumnName("time")
                  .HasColumnType("timestamptz")
                  .HasDefaultValueSql("now()");

            entity.Property(e => e.IsCheck)
                  .HasColumnName("is_check")
                  .HasDefaultValue(false);

            entity.Property(e => e.Status)
                  .HasColumnName("status")
                  .HasMaxLength(20)
                  .HasDefaultValue("active");

            entity.HasIndex(e => e.UserId)
                  .HasDatabaseName("ix_notifications_user_id");

            entity.HasIndex(e => e.RoleId)
                  .HasDatabaseName("ix_notifications_role_id");

            entity.HasIndex(e => e.Time)
                  .HasDatabaseName("ix_notifications_time_desc");
        });

        modelBuilder.Entity<estimate_config>(e =>
        {
            e.HasKey(x => new { x.config_group, x.config_key });

            e.Property(x => x.config_group).HasMaxLength(100);
            e.Property(x => x.config_key).HasMaxLength(120);
            e.Property(x => x.value_num).HasColumnType("numeric(18,6)");
            e.Property(e => e.updated_at).HasColumnType("timestamp without time zone");
        });

        modelBuilder.Entity<missing_material>(entity =>
        {
            entity.ToTable("missing_materials", "AMMS_DB");
            entity.HasKey(e => e.miss_id);
            entity.Property(e => e.miss_id)
                  .HasColumnName("miss_id")
                  .ValueGeneratedOnAdd();
            entity.Property(e => e.material_id).HasColumnName("material_id").IsRequired();
            entity.Property(e => e.material_name)
                  .HasColumnName("material_name")
                  .IsRequired();
            entity.Property(e => e.needed)
                  .HasColumnName("needed")
                  .HasColumnType("numeric(18,4)")
                  .HasDefaultValue(0m);
            entity.Property(e => e.available)
                  .HasColumnName("available")
                  .HasColumnType("numeric(18,4)")
                  .HasDefaultValue(0m);
            entity.Property(e => e.quantity)
                  .HasColumnName("quantity")
                  .HasColumnType("numeric(18,4)")
                  .HasDefaultValue(0m);
            entity.Property(e => e.unit)
                  .HasColumnName("unit")
                  .IsRequired();
            entity.Property(e => e.request_date)
                  .HasColumnName("request_date")
                  .HasColumnType("timestamp without time zone");
            entity.Property(e => e.total_price)
                  .HasColumnName("total_price")
                  .HasColumnType("numeric(18,2)")
                  .HasDefaultValue(0m);
            entity.Property(e => e.is_buy)
                  .HasColumnName("is_buy")
                  .HasDefaultValue(false);
            entity.Property(e => e.created_at)
                  .HasColumnName("created_at")
                  .HasColumnType("timestamp without time zone")
                  .HasDefaultValueSql("now()");
            entity.HasIndex(e => e.material_id).HasDatabaseName("ix_missing_materials_material_id");
            entity.HasIndex(e => e.request_date).HasDatabaseName("ix_missing_materials_request_date");
            entity.HasIndex(e => e.created_at).HasDatabaseName("ix_missing_materials_created_at");
        });
        modelBuilder.Entity<product>(entity =>
        {
            entity.ToTable("products", "AMMS_DB");
            entity.HasKey(e => e.product_id).HasName("products_pkey");
            entity.Property(e => e.product_id)
                  .ValueGeneratedOnAdd();
            entity.Property(e => e.product_type_id)
                  .IsRequired();
            entity.Property(e => e.code)
                  .HasMaxLength(64);
            entity.Property(e => e.name)
                  .IsRequired()
                  .HasMaxLength(255);
            entity.Property(e => e.description)
                  .HasColumnType("text");
            entity.Property(e => e.is_active)
                  .IsRequired()
                  .HasDefaultValue(true);
            entity.Property(e => e.created_at)
                  .HasColumnType("timestamp without time zone")
                  .HasDefaultValueSql("(NOW() AT TIME ZONE 'UTC')");

            entity.Property(e => e.updated_at)
                  .HasColumnType("timestamp without time zone");

            entity.HasOne(d => d.product_type)
                  .WithMany(p => p.products)
                  .HasForeignKey(d => d.product_type_id)
                  .OnDelete(DeleteBehavior.Restrict)
                  .HasConstraintName("fk_products_product_type");
        });

        modelBuilder.Entity<production_calendar>(entity =>
        {
            entity.HasKey(e => e.calendar_date).HasName("production_calendar_pkey");

            entity.ToTable("production_calendar", "AMMS_DB");

            entity.Property(e => e.calendar_date)
                .HasColumnType("date");

            entity.Property(e => e.holiday_name)
                .HasMaxLength(255);

            entity.Property(e => e.holiday_type)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(e => e.is_non_working_day)
                .IsRequired()
                .HasDefaultValue(false);

            entity.Property(e => e.is_manual_override)
                .IsRequired()
                .HasDefaultValue(false);

            entity.Property(e => e.created_at)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");

            entity.Property(e => e.updated_at)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");

            entity.HasIndex(e => e.holiday_type)
                .HasDatabaseName("ix_production_calendar_type");

            entity.HasIndex(e => e.is_manual_override)
                .HasDatabaseName("ix_production_calendar_manual");
        });

        modelBuilder.Entity<prod_order>(entity =>
        {
            entity.ToTable("prod_orders", "AMMS_DB");
            entity.HasKey(x => x.id);

            entity.HasIndex(x => new { x.prod_id, x.order_id })
                .IsUnique();

            entity.HasOne(x => x.production)
                .WithMany(x => x.prod_orders)
                .HasForeignKey(x => x.prod_id);

            entity.Property(x => x.created_at)
                .HasColumnType("timestamp without time zone")
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(x => x.order)
                .WithMany()
                .HasForeignKey(x => x.order_id);

            entity.HasOne(x => x.single_production)
                .WithMany()
                .HasForeignKey(x => x.single_prod_id);
        });

        modelBuilder.Entity<task_link>(entity =>
        {
            entity.ToTable("task_links", "AMMS_DB");

            entity.HasKey(x => x.id);

            entity.Property(x => x.created_at)
                .HasColumnType("timestamp without time zone")
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(x => new { x.group_task_id, x.single_task_id })
                .IsUnique();
        });

        modelBuilder.Entity<task_qty>(entity =>
        {
            entity.ToTable("task_qtys", "AMMS_DB");

            entity.Property(x => x.created_at)
                .HasColumnType("timestamp without time zone")
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasKey(x => x.id);
        });

        modelBuilder.Entity<sub_product>(entity =>
        {
            entity.ToTable("sub_product", "AMMS_DB");

            entity.HasKey(e => e.id).HasName("sub_product_pkey");

            entity.Property(e => e.id)
                  .ValueGeneratedOnAdd();

            entity.Property(e => e.product_process)
                  .HasMaxLength(100);

            entity.Property(e => e.quantity)
                  .HasDefaultValue(0);

            entity.Property(e => e.is_active)
                  .HasDefaultValue(true);

            entity.Property(e => e.description)
                  .HasMaxLength(255);

            entity.Property(e => e.updated_at)
                  .HasColumnType("timestamp without time zone")
                  .HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => e.product_type_id)
                  .HasDatabaseName("ix_sub_product_product_type_id");

            entity.HasIndex(e => e.is_active)
                  .HasDatabaseName("ix_sub_product_is_active");

            entity.HasOne(d => d.product_type)
                  .WithMany(p => p.sub_products)
                  .HasForeignKey(d => d.product_type_id)
                  .OnDelete(DeleteBehavior.Restrict)
                  .HasConstraintName("fk_sub_product_product_type");
        });
        OnModelCreatingPartial(modelBuilder);
    }
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
