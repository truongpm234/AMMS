using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Entities.AMMS.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

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


    public virtual DbSet<delivery> deliveries { get; set; }

    public virtual DbSet<material> materials { get; set; }

    public virtual DbSet<order> orders { get; set; }

    public virtual DbSet<order_item> order_items { get; set; }

    public virtual DbSet<order_request> order_requests { get; set; }

    public virtual DbSet<product_type> product_types { get; set; }

    public virtual DbSet<product_type_process> product_type_processes { get; set; }

    public virtual DbSet<production> productions { get; set; }

    public virtual DbSet<purchase> purchases { get; set; }

    public virtual DbSet<purchase_item> purchase_items { get; set; }

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

    public virtual DbSet<process_cost_rule> process_cost_rules { get; set; }

    public virtual DbSet<product_template> product_templates { get; set; } = null!;

    public virtual DbSet<missing_material> missing_materials { get; set; } = null!;

    public virtual DbSet<estimate_config> estimate_config { get; set; } = null!;

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

        modelBuilder.Entity<delivery>(entity =>
        {
            entity.HasKey(e => e.delivery_id).HasName("deliveries_pkey");

            entity.Property(e => e.carrier).HasMaxLength(100);
            entity.Property(e => e.ship_date).HasColumnType("timestamp without time zone");
            entity.Property(e => e.status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'Ready'::character varying");
            entity.Property(e => e.tracking_code).HasMaxLength(50);

            entity.HasOne(d => d.order).WithMany(p => p.deliveries)
                .HasForeignKey(d => d.order_id)
                .HasConstraintName("deliveries_order_id_fkey");
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
            entity.Property(e => e.order_date)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.payment_status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'Unpaid'::character varying");
            entity.Property(e => e.status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'New'::character varying");
            entity.Property(e => e.total_amount).HasPrecision(15, 2);

            entity.HasOne(d => d.consultant).WithMany(p => p.orders)
                .HasForeignKey(d => d.consultant_id)
                .HasConstraintName("orders_consultant_id_fkey");

            entity.HasOne(d => d.quote).WithMany(p => p.orders)
                .HasForeignKey(d => d.quote_id)
                .HasConstraintName("orders_quote_id_fkey");
        });       

        modelBuilder.Entity<order>(entity =>
        {
            entity.HasKey(e => e.order_id).HasName("orders_pkey");

            entity.HasIndex(e => e.code, "orders_code_key").IsUnique();

            entity.Property(e => e.code).HasMaxLength(20);
            entity.Property(e => e.delivery_date).HasColumnType("timestamp without time zone");
            entity.Property(e => e.order_date)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.payment_status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'Unpaid'::character varying");
            entity.Property(e => e.status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'New'::character varying");
            entity.Property(e => e.total_amount).HasPrecision(15, 2);

            entity.HasOne(d => d.consultant).WithMany(p => p.orders)
                .HasForeignKey(d => d.consultant_id)
                .HasConstraintName("orders_consultant_id_fkey");

            entity.HasOne(d => d.quote).WithMany(p => p.orders)
                .HasForeignKey(d => d.quote_id)
                .HasConstraintName("orders_quote_id_fkey");
            entity.HasOne(o => o.production)
                .WithMany()
                .HasForeignKey(o => o.production_id)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_orders_production");
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
            entity.Property(e => e.order_request_date).HasColumnType("timestamp without time zone");
            entity.Property(e => e.product_name).HasMaxLength(200);
            entity.Property(e => e.product_type).HasMaxLength(50);
            entity.Property(e => e.number_of_plates).HasDefaultValue(0);
            entity.Property(e => e.production_processes);
            entity.Property(e => e.coating_type)
                .HasMaxLength(20)
                .HasDefaultValue("NONE");
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
            entity.Property(e => e.start_date).HasColumnType("timestamp without time zone");
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
        });

        modelBuilder.Entity<purchase>(entity =>
        {
            entity.HasKey(e => e.purchase_id).HasName("purchases_pkey");

            entity.HasIndex(e => e.code, "purchases_code_key").IsUnique();

            entity.Property(e => e.code).HasMaxLength(20);
            entity.Property(e => e.created_at)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'Pending'::character varying");

            entity.HasOne(d => d.created_byNavigation).WithMany(p => p.purchases)
                .HasForeignKey(d => d.created_by)
                .HasConstraintName("purchases_created_by_fkey");

            entity.HasOne(d => d.supplier).WithMany(p => p.purchases)
                .HasForeignKey(d => d.supplier_id)
                .HasConstraintName("purchases_supplier_id_fkey");
        });

        modelBuilder.Entity<purchase_item>(entity =>
        {
            entity.HasKey(e => e.id).HasName("purchase_items_pkey");

            entity.Property(e => e.price).HasPrecision(15, 2);
            entity.Property(e => e.qty_ordered).HasPrecision(10, 2);

            entity.HasOne(d => d.material).WithMany(p => p.purchase_items)
                .HasForeignKey(d => d.material_id)
                .HasConstraintName("purchase_items_material_id_fkey");

            entity.HasOne(d => d.purchase).WithMany(p => p.purchase_items)
                .HasForeignKey(d => d.purchase_id)
                .HasConstraintName("purchase_items_purchase_id_fkey");
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

            entity.HasOne(d => d.consultant).WithMany(p => p.quotes)
                .HasForeignKey(d => d.consultant_id)
                .HasConstraintName("quotes_consultant_id_fkey");

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

            entity.HasOne(d => d.purchase)
                .WithMany(p => p.stock_moves)
                .HasForeignKey(d => d.purchase_id)
                .HasConstraintName("stock_moves_purchase_id_fkey");
        });

        modelBuilder.Entity<supplier>(entity =>
        {
            entity.HasKey(e => e.supplier_id).HasName("suppliers_pkey");

            entity.Property(e => e.contact_person).HasMaxLength(100);
            entity.Property(e => e.email).HasMaxLength(100);
            entity.Property(e => e.type).HasMaxLength(50);
            entity.Property(e => e.name).HasMaxLength(150);
            entity.Property(e => e.phone).HasMaxLength(20);
            entity.Property(e => e.rating)
            .HasColumnType("numeric(3,2)")
            .HasDefaultValue(0);
        });

        modelBuilder.Entity<task>(entity =>
        {
            entity.HasKey(e => e.task_id).HasName("tasks_pkey");

            entity.Property(e => e.end_time).HasColumnType("timestamp without time zone");
            entity.Property(e => e.machine).HasMaxLength(50);
            entity.Property(e => e.name).HasMaxLength(100);
            entity.Property(e => e.start_time).HasColumnType("timestamp without time zone");
            entity.Property(e => e.status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'Unassigned'::character varying");
         
            entity.HasOne(d => d.process).WithMany(p => p.tasks)
                .HasForeignKey(d => d.process_id)
                .HasConstraintName("tasks_process_id_fkey");

            entity.HasOne(d => d.prod).WithMany(p => p.tasks)
                .HasForeignKey(d => d.prod_id)
                .HasConstraintName("tasks_prod_id_fkey");
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
        });

        modelBuilder.Entity<user>(entity =>
        {
            entity.HasKey(e => e.user_id).HasName("users_pkey");

            entity.HasIndex(e => e.username, "users_username_key").IsUnique();

            entity.Property(e => e.created_at)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.full_name).HasMaxLength(100);
            entity.Property(e => e.is_active).HasDefaultValue(true);
            entity.Property(e => e.password_hash).HasMaxLength(255);
            entity.Property(e => e.username).HasMaxLength(50);

            entity.HasOne(d => d.role).WithMany(p => p.users)
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
            entity.Property(e => e.overhead_percent).HasPrecision(5, 2).HasDefaultValue(5);
            entity.Property(e => e.overhead_cost).HasPrecision(18, 2).HasDefaultValue(0);
            entity.Property(e => e.base_cost).HasPrecision(18, 2);
            entity.Property(e => e.is_rush).HasDefaultValue(false);
            entity.Property(e => e.rush_percent).HasPrecision(5, 2).HasDefaultValue(0);
            entity.Property(e => e.rush_amount).HasPrecision(18, 2).HasDefaultValue(0);
            entity.Property(e => e.days_early).HasDefaultValue(0);
            entity.Property(e => e.subtotal).HasPrecision(18, 2).HasDefaultValue(0);
            entity.Property(e => e.discount_percent).HasPrecision(5, 2).HasDefaultValue(0);
            entity.Property(e => e.discount_amount).HasPrecision(18, 2).HasDefaultValue(0);
            entity.Property(e => e.final_total_cost).HasPrecision(18, 2).HasDefaultValue(0);
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
            .HasColumnType("numeric(18,2)")
            .HasComputedColumnSql("ROUND(final_total_cost * 0.30, 0)", stored: true)
            .ValueGeneratedOnAddOrUpdate();
            entity.Property(x => x.deposit_amount).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
            entity.Property(x => x.deposit_amount).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
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

        modelBuilder.Entity<process_cost_rule>(entity =>
        {
            entity.HasKey(e => e.process_code).HasName("process_cost_rule_pkey");
            entity.Property(e => e.process_code)
                  .HasMaxLength(50);
            entity.Property(e => e.process_name)
                  .HasMaxLength(255);
            entity.Property(e => e.unit)
                  .HasMaxLength(20);
            entity.Property(e => e.unit_price)
                  .HasPrecision(18, 2);
        });

        modelBuilder.Entity<product_template>(entity =>
        {
            entity.HasKey(e => e.design_profile_id);
            entity.HasOne(e => e.product_type)
                  .WithMany(p => p.product_type_design_profiles)
                  .HasForeignKey(e => e.product_type_id);
        });

        modelBuilder.Entity<estimate_config>(e =>
        {
            e.HasKey(x => new { x.config_group, x.config_key });

            e.Property(x => x.config_group).HasMaxLength(100);
            e.Property(x => x.config_key).HasMaxLength(120);
            e.Property(x => x.value_num).HasColumnType("numeric(18,6)");
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
                  .HasColumnType("timestamptz");
            entity.Property(e => e.total_price)
                  .HasColumnName("total_price")
                  .HasColumnType("numeric(18,2)")
                  .HasDefaultValue(0m);
            entity.Property(e => e.is_buy)
                  .HasColumnName("is_buy")
                  .HasDefaultValue(false);
            entity.Property(e => e.created_at)
                  .HasColumnName("created_at")
                  .HasColumnType("timestamptz")
                  .HasDefaultValueSql("now()");
            entity.HasIndex(e => e.material_id).HasDatabaseName("ix_missing_materials_material_id");
            entity.HasIndex(e => e.request_date).HasDatabaseName("ix_missing_materials_request_date");
            entity.HasIndex(e => e.created_at).HasDatabaseName("ix_missing_materials_created_at");
        });
        OnModelCreatingPartial(modelBuilder);
    }


    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
